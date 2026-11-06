using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using NAudio.Wasapi.CoreAudioApi.Interfaces;
using NAudio.Wave;
using Odx.Core;

namespace Odx.Recorder;

/// <summary>Raw PCM that the video's ffmpeg reads as its audio input, from a named pipe.</summary>
internal sealed record AudioInput(string SampleFormat, int SampleRate, int Channels, string Pipe);

/// <summary>
/// Streams what one process tree plays (WASAPI process loopback: the game only, not voice chat, browsers or system
/// sounds, on whatever device it plays), or else the whole default playback device, as PCM into a named pipe that the
/// video's ffmpeg muxes into video.mp4, with sample 0 at <see cref="SetOrigin"/> on the QPC clock (the first video grid
/// tick). Packets are placed by their QPC capture time (<see cref="AudioTimeline"/>), or by their arrival when the
/// device's times turn out not to be on the QPC clock. WASAPI delivers nothing while nothing plays, so silence is written
/// up to <see cref="LagMs"/> behind now: the audio keeps pace with the video and ffmpeg never waits for it.
/// The capture thread only queues bytes; a writer thread owns the pipe, so a stalled ffmpeg never stalls the capture
/// (WASAPI's buffer would overflow and lose sound). <see cref="Status"/> tells what was recorded and how it went.
/// </summary>
internal sealed class AudioLoopback : IDisposable
{
    private const double QpcToleranceMs = 20, ArrivalToleranceMs = 100;
    // Longer than a packet takes to arrive (10 ms polling + the device period), so real sound is not overwritten.
    private const double LagMs = 100;
    /// <summary>A packet whose capture time is this far from now is not on the QPC clock.</summary>
    private const double MaxQpcSkewMs = 1000;
    private const long BufferHns = 20_000_000;  // 2 s of WASAPI buffer (a hint; a stalled capture thread then loses less)
    // Process loopback is documented from build 20348 (Windows 11 / Server 2022) but works from Windows 10 2004 (19041),
    // the build OBS's and GStreamer's process capture require; gating on 20348 sent every Windows 10 PC to the device
    // fallback, voice chat included (test of 2026-11-06).
    private const int ProcessLoopbackMinBuild = 19041;
    private const int QueuedChunks = 4096;        // ~40 s of 10 ms packets, or 4096 s of 1 s silence chunks
    private const double SilentDbfs = -60;

    private readonly AudioClient _client;
    private readonly AudioCaptureClient _capture;
    private readonly NamedPipeServerStream _pipe;
    private readonly Task _connected;
    private readonly BlockingCollection<byte[]> _queue = new(QueuedChunks);
    private readonly Thread _thread, _writer;
    private readonly AutoResetEvent _packetReady = new(false);
    private readonly List<(byte[] Data, double QpcMs, bool Silent)> _early = [];
    private readonly object _gate = new();
    private readonly int _bytesPerFrame;
    private readonly bool _float;
    private readonly AudioTimeline _timeline;
    private volatile bool _stopping;
    private string? _timing;
    private double? _qpcOffsetMs;
    private long _packets, _discontinuities, _droppedBytes, _silentFrames;
    private double _peak;

    public AudioInput Input { get; }
    /// <summary>The game process recorded; null when the whole default playback device is.</summary>
    public int? ProcessId { get; }
    /// <summary>The playback device recorded; null when only the game process is.</summary>
    public string? Device { get; }
    public double? OriginMs => _timeline.OriginMs;
    /// <summary>Why audio stopped being written (ffmpeg closed the pipe or never opened it).</summary>
    public string? Error { get; private set; }

    /// <param name="processId">The game's process; processes it starts are included.</param>
    /// <exception cref="NotSupportedException">Windows is older than 10 2004 (build 19041), which added process loopback.</exception>
    public AudioLoopback(int processId)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, ProcessLoopbackMinBuild))
            throw new NotSupportedException("recording only the game's audio needs Windows 10 2004 (build 19041) or newer");
        ProcessId = processId;
        _client = ProcessLoopback.Activate(processId);
        // A process loopback client has no mix format: Windows converts the game's audio to the one asked for.
        var format = WaveFormat.CreateIeeeFloatWaveFormat(48000, 2);
        _bytesPerFrame = format.BlockAlign;
        _float = true;
        _client.Initialize(AudioClientShareMode.Shared,
            AudioClientStreamFlags.Loopback | AudioClientStreamFlags.EventCallback | AudioClientStreamFlags.AutoConvertPcm,
            BufferHns, 0, format, Guid.Empty);
        _client.SetEventHandle(_packetReady.SafeWaitHandle.DangerousGetHandle());
        _capture = _client.AudioCaptureClient;
        (_pipe, _connected, Input) = OpenPipe("f32le", format);
        _timeline = new AudioTimeline(format.SampleRate, QpcToleranceMs);
        (_thread, _writer) = Threads();
    }

    /// <summary>Everything the default playback device plays (voice chat and other apps included); polled every 10 ms.</summary>
    /// <exception cref="NotSupportedException">The device's mix format is neither 32-bit float nor 16-bit PCM.</exception>
    public AudioLoopback()
    {
        using var enumerator = new MMDeviceEnumerator();
        using var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
        Device = device.FriendlyName;
        _client = device.AudioClient;
        var format = _client.MixFormat;
        string sampleFormat = format switch
        {
            { BitsPerSample: 32 } when IsFloat(format) => "f32le",
            { BitsPerSample: 16 } when !IsFloat(format) => "s16le",
            _ => throw new NotSupportedException($"audio mix format {format} is not supported"),
        };
        _bytesPerFrame = format.BlockAlign;
        _float = sampleFormat == "f32le";
        _client.Initialize(AudioClientShareMode.Shared, AudioClientStreamFlags.Loopback, BufferHns, 0, format, Guid.Empty);
        _capture = _client.AudioCaptureClient;
        (_pipe, _connected, Input) = OpenPipe(sampleFormat, format);
        _timeline = new AudioTimeline(format.SampleRate, QpcToleranceMs);
        (_thread, _writer) = Threads();
    }

    private (Thread Capture, Thread Writer) Threads() =>
        (new Thread(Run) { IsBackground = true, Name = "audio loopback" },
         new Thread(WritePipe) { IsBackground = true, Name = "audio pipe" });

    private static (NamedPipeServerStream, Task, AudioInput) OpenPipe(string sampleFormat, WaveFormat format)
    {
        string name = "odx-audio-" + Guid.NewGuid().ToString("N");
        var pipe = new NamedPipeServerStream(name, PipeDirection.Out, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous,
            inBufferSize: 0, outBufferSize: 1 << 20);
        return (pipe, pipe.WaitForConnectionAsync(), new AudioInput(sampleFormat, format.SampleRate, format.Channels, @"\\.\pipe\" + name));
    }

    private static readonly Guid SubtypeIeeeFloat = new("00000003-0000-0010-8000-00aa00389b71");  // KSDATAFORMAT_SUBTYPE_IEEE_FLOAT

    private static bool IsFloat(WaveFormat format) =>
        format.Encoding == WaveFormatEncoding.IeeeFloat ||
        format is WaveFormatExtensible { SubFormat: var s } && s == SubtypeIeeeFloat;

    public void Start()
    {
        _client.Start();
        _thread.Start();
        _writer.Start();
    }

    /// <summary>QPC time (ms) of sample 0; packets captured before it are held until it is known.</summary>
    public void SetOrigin(double qpcMs)
    {
        lock (_gate) _timeline.SetOrigin(qpcMs);
    }

    /// <summary>Pads to <paramref name="stopQpcMs"/> and closes the pipe, so ffmpeg sees the audio end.</summary>
    public void Finish(double stopQpcMs)
    {
        _stopping = true;
        _thread.Join();
        _client.Stop();
        lock (_gate)
        {
            _timeline.SetOrigin(stopQpcMs);
            PlaceEarly();
            Enqueue(Silence(_timeline.SilenceTo(stopQpcMs)));
        }
        _queue.CompleteAdding();
        // A writer still blocked in Write holds the pipe open whatever Dispose does: the pipe closes once ffmpeg, which
        // stopped reading it, has exited or been killed (Dispose).
        if (_writer.Join(TimeSpan.FromSeconds(10))) _pipe.Dispose();
        else Error ??= "ffmpeg stopped reading the audio pipe";
    }

    /// <summary>What is being recorded, and the counters so far (final after <see cref="Finish"/>).</summary>
    public AudioStatus Status(string? fallbackReason = null)
    {
        lock (_gate)
        {
            long written = _timeline.FramesWritten;
            return new AudioStatus(ProcessId is null ? AudioStatus.FromDevice : AudioStatus.FromProcess, ProcessId, Device, fallbackReason,
                Error, _timing, _packets, _discontinuities, _timeline.PaddedFrames, _timeline.TrimmedFrames, _droppedBytes,
                _packets == 0 ? null : Math.Round(Dbfs(_peak), 1),
                written == 0 ? null : Math.Round((double)Math.Min(written, _silentFrames + _timeline.PaddedFrames) / written, 4),
                _qpcOffsetMs is double q ? Math.Round(q, 1) : null);
        }
    }

    private static double Dbfs(double peak) => peak <= 0 ? -100 : Math.Max(-100, 20 * Math.Log10(peak));

    private void Run()
    {
        while (!_stopping)
        {
            _packetReady.WaitOne(10);
            while (_capture.GetNextPacketSize() > 0)
            {
                // qpcPosition is in 100 ns units (IAudioCaptureClient::GetBuffer).
                IntPtr buffer = _capture.GetBuffer(out int frames, out AudioClientBufferFlags flags, out _, out long qpc);
                var data = new byte[frames * _bytesPerFrame];
                bool silent = (flags & AudioClientBufferFlags.Silent) != 0;
                if (!silent) Marshal.Copy(buffer, data, 0, data.Length);
                _capture.ReleaseBuffer(frames);
                double now = QpcClock.NowMs(), at = Timestamp(qpc / 10_000.0, frames, now);
                double peak = silent ? 0 : Peak(data);
                lock (_gate)
                {
                    _packets++;
                    if ((flags & AudioClientBufferFlags.DataDiscontinuity) != 0) _discontinuities++;
                    _peak = Math.Max(_peak, peak);
                    silent |= Dbfs(peak) < SilentDbfs;
                    if (silent) _silentFrames += frames;
                    if (_timeline.OriginMs is null) _early.Add((data, at, silent));
                    else
                    {
                        PlaceEarly();
                        Place(data, at);
                    }
                }
            }
            lock (_gate)
            {
                if (_timeline.OriginMs is null) continue;
                PlaceEarly();
                Enqueue(Silence(_timeline.SilenceTo(QpcClock.NowMs() - LagMs)));
            }
        }
    }

    /// <summary>
    /// Where the packet was captured on the QPC clock. The first packet decides whether the device's qpcPosition is on
    /// that clock (within <see cref="MaxQpcSkewMs"/> of now); if not, packets are placed by when they arrived, a device
    /// period late at most, with a wider tolerance for the arrival jitter.
    /// </summary>
    private double Timestamp(double qpcMs, int frames, double nowMs)
    {
        if (_timing is null)
        {
            _qpcOffsetMs = qpcMs - nowMs;
            bool onQpc = qpcMs > 0 && Math.Abs(qpcMs - nowMs) <= MaxQpcSkewMs;
            _timing = onQpc ? "qpc" : "arrival";
            if (!onQpc) _timeline.ToleranceMs = ArrivalToleranceMs;
        }
        return _timing == "qpc" ? qpcMs : nowMs - frames * 1000.0 / Input.SampleRate;
    }

    private double Peak(byte[] data)
    {
        double peak = 0;
        if (_float)
            foreach (float v in MemoryMarshal.Cast<byte, float>(data)) peak = Math.Max(peak, Math.Abs(v));
        else
            foreach (short v in MemoryMarshal.Cast<byte, short>(data)) peak = Math.Max(peak, Math.Abs(v) / 32768.0);
        return peak;
    }

    private void PlaceEarly()
    {
        foreach (var (data, at, _) in _early) Place(data, at);
        _early.Clear();
    }

    private void Place(byte[] data, double qpcMs)
    {
        var (pad, skip) = _timeline.Place(qpcMs, data.Length / _bytesPerFrame);
        Enqueue(Silence(pad));
        if (skip == 0) Enqueue(data);
        else if (skip * _bytesPerFrame < data.Length) Enqueue(data[(skip * _bytesPerFrame)..]);
    }

    private IEnumerable<byte[]> Silence(long frames)
    {
        while (frames > 0)
        {
            int chunk = (int)Math.Min(frames, Input.SampleRate);
            yield return new byte[chunk * _bytesPerFrame];
            frames -= chunk;
        }
    }

    private void Enqueue(IEnumerable<byte[]> chunks)
    {
        foreach (var chunk in chunks) Enqueue(chunk);
    }

    /// <summary>Queues for the writer; when it is a whole queue behind, the oldest chunk goes (counted) so capture never blocks.</summary>
    private void Enqueue(byte[] chunk)
    {
        if (chunk.Length == 0 || _queue.IsAddingCompleted) return;
        while (!_queue.TryAdd(chunk))
        {
            if (_queue.TryTake(out var dropped)) _droppedBytes += dropped.Length;
        }
    }

    private void WritePipe()
    {
        // ffmpeg opens the pipe once it starts, with the first video frame; the queue holds the sound until then.
        if (!_connected.Wait(TimeSpan.FromSeconds(30)))
        {
            Error = "ffmpeg did not open the audio pipe";
            foreach (var _ in _queue.GetConsumingEnumerable()) { }
            return;
        }
        foreach (var chunk in _queue.GetConsumingEnumerable())
        {
            if (Error is not null) continue;  // drain, so the capture thread's queue never fills for nothing
            try
            {
                _pipe.Write(chunk, 0, chunk.Length);
            }
            catch (IOException e)
            {
                Error = $"audio pipe closed: {e.Message}";
            }
        }
    }

    public void Dispose()
    {
        _queue.CompleteAdding();
        _client.Dispose();
        _pipe.Dispose();
        _packetReady.Dispose();
        _queue.Dispose();
    }
}

/// <summary>
/// Activates an audio client on one process tree's audio (AUDIOCLIENT_ACTIVATION_TYPE_PROCESS_LOOPBACK). NAudio has the
/// COM interfaces but keeps the activation parameters internal.
/// </summary>
internal static class ProcessLoopback
{
    private const string VirtualDevice = @"VAD\Process_Loopback";  // VIRTUAL_AUDIO_DEVICE_PROCESS_LOOPBACK
    private static readonly Guid IidAudioClient = new("1CB9AD4C-DBFA-4C32-B178-C2F568A703B2");

    // AUDIOCLIENT_ACTIVATION_PARAMS: AUDIOCLIENT_ACTIVATION_TYPE_PROCESS_LOOPBACK (1), PROCESS_LOOPBACK_MODE_INCLUDE_TARGET_PROCESS_TREE (0).
    [StructLayout(LayoutKind.Sequential)]
    private struct ActivationParams
    {
        public int ActivationType;
        public uint TargetProcessId;
        public int LoopbackMode;
    }

    // PROPVARIANT holding a VT_BLOB, x64 layout (the package is win-x64 only).
    [StructLayout(LayoutKind.Explicit)]
    private struct BlobPropVariant
    {
        [FieldOffset(0)] public ushort Vt;
        [FieldOffset(8)] public int Size;
        [FieldOffset(16)] public IntPtr Data;
    }

    [DllImport("Mmdevapi.dll", ExactSpelling = true, PreserveSig = false)]
    private static extern void ActivateAudioInterfaceAsync([MarshalAs(UnmanagedType.LPWStr)] string deviceInterfacePath,
        [MarshalAs(UnmanagedType.LPStruct)] Guid riid, IntPtr activationParams,
        IActivateAudioInterfaceCompletionHandler completionHandler, out IActivateAudioInterfaceAsyncOperation operation);

    /// <exception cref="TimeoutException">Windows did not complete the activation within 5 s.</exception>
    public static AudioClient Activate(int processId)
    {
        int paramsSize = Marshal.SizeOf<ActivationParams>();
        IntPtr activationParams = Marshal.AllocHGlobal(paramsSize);
        IntPtr variant = Marshal.AllocHGlobal(Marshal.SizeOf<BlobPropVariant>());
        try
        {
            Marshal.StructureToPtr(new ActivationParams { ActivationType = 1, TargetProcessId = (uint)processId }, activationParams, false);
            Marshal.StructureToPtr(new BlobPropVariant { Vt = 65 /* VT_BLOB */, Size = paramsSize, Data = activationParams }, variant, false);
            var handler = new CompletionHandler();
            ActivateAudioInterfaceAsync(VirtualDevice, IidAudioClient, variant, handler, out _);
            return new AudioClient(handler.Done.WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult());
        }
        finally
        {
            Marshal.FreeHGlobal(variant);
            Marshal.FreeHGlobal(activationParams);
        }
    }

    // Must be agile: Windows calls ActivateCompleted from one of its own threads.
    private sealed class CompletionHandler : IActivateAudioInterfaceCompletionHandler, IAgileObject
    {
        private readonly TaskCompletionSource<IAudioClient> _done = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<IAudioClient> Done => _done.Task;

        public void ActivateCompleted(IActivateAudioInterfaceAsyncOperation operation)
        {
            operation.GetActivateResult(out int hr, out object client);
            if (hr < 0) _done.SetException(Marshal.GetExceptionForHR(hr)!);
            else _done.SetResult((IAudioClient)client);
        }
    }

    [ComImport, Guid("94EA2B94-E9CC-49E0-C0FF-EE64CA8F5B90"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAgileObject { }
}
