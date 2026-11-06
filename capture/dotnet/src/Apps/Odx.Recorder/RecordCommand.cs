// odx record --out <dir> (--window <process name> | --monitor) [--fps 60] [--encoder auto|x264|nvenc|mf] [--ffmpeg <path>]
//            [--cursor] [--audio [--audio-process <process name>]] [--obs <host:port>] [--seconds N] [--stop-event <name>]
//            [--json-status] [--probe]
//
// Records a game window, or a monitor (the one the --audio-process game's window is on, else the primary), with
// Windows.Graphics.Capture. A window must be windowed or borderless: an exclusive-fullscreen window gives black frames,
// which --probe tells (captures up to 2 s, prints a CaptureProbe JSON line, records nothing). A window whose size
// changes while recording (alt-tab out of fullscreen) keeps the recording going on its last frame until the size is
// back (size_changed_frames in recording.json). Every captured frame carries its QPC
// capture time (SystemRelativeTime), the same clock as odx input. Frames are resampled onto a constant-rate grid
// (GridResampler) and piped to ffmpeg, so the MP4 is CFR and its frame N is row N of video_frames.csv. Writes:
//   <dir>/video.mp4         CFR H.264, no B-frames; --audio: plus an AAC track, sample 0 at video frame 0, of the game
//                           process's own sound (--audio-process, else the --window process; not voice chat, browsers or
//                           other apps), or of everything the default playback device plays when no game process is known
//   <dir>/video_frames.csv  frame_index,t_grid_qpc_ms,t_source_qpc_ms,duplicate
//   <dir>/recording.json    capture id, capture and screen size, fps, encoder, start time (UTC + QPC),
//                           counts of duplicated/dropped frames, audio (null without --audio: sample rate, channels,
//                           start, and a status: source process|device|none, fallback reason, timing, counters)
// --obs: OBS records instead, with its own scene, output and audio settings (ObsRecorder); the WebSocket password, if
// any, comes from the ODX_OBS_PASSWORD environment variable. The same three files are written.
// Stop with Ctrl+C, --seconds, --stop-event (a named event set by Odx.App), or by closing the captured window.
// --json-status: stdout carries only RecorderStatus JSON lines (started, progress every second, stopped).
using System.Diagnostics;
using System.Text.Json;
using Odx.Core;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;

namespace Odx.Recorder;

public static class RecordCommand
{
    // Every wait on the stop path has a limit, so a stuck ffmpeg ends in a recording.json that says which phase stuck
    // (StopTiming) rather than in the capture host killing this process a minute later and knowing nothing.
    private static readonly TimeSpan RentWait = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan DrainTimeout = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan FfmpegExitTimeout = TimeSpan.FromSeconds(30);

    public static int Run(string[] args)
    {
        var opts = Options.Parse(args);
        if (opts is null)
        {
            Console.Error.WriteLine("usage: odx record --out <dir> (--window <process name> | --monitor) [--fps 60] " +
                                    "[--encoder auto|x264|nvenc|mf] [--ffmpeg <path>] [--cursor] [--audio [--audio-process <process name>]] [--obs <host:port>] [--seconds N] [--stop-event <name>] [--json-status] [--probe]");
            return 2;
        }
        if (opts.Obs is string obs) return RunObs(opts, obs);
        if (!GraphicsCaptureSession.IsSupported())
        {
            Console.Error.WriteLine("Windows.Graphics.Capture is not supported on this system");
            return 1;
        }

        var encoderName = Task.Run(() => EncoderChoice.Resolve(opts.Ffmpeg, opts.Encoder));
        CaptureInterop.UsePhysicalPixels();
        GraphicsCaptureItem item;
        IntPtr monitor, window = IntPtr.Zero;
        int? gameProcessId = null;
        string source = $"window:{opts.Window}";
        if (opts.Window is string processName)
        {
            var proc = Process.GetProcessesByName(Path.GetFileNameWithoutExtension(processName))
                .FirstOrDefault(p => p.MainWindowHandle != IntPtr.Zero);
            if (proc is null)
            {
                Console.Error.WriteLine($"no window found for process '{processName}'");
                return 1;
            }
            window = proc.MainWindowHandle;
            gameProcessId = CaptureInterop.ProcessOf(window);  // the window's owner, not the first process of that name
            item = CaptureInterop.ItemForWindow(window);
            monitor = CaptureInterop.MonitorOf(window);
        }
        else
        {
            // The game's monitor when its process is known (a fullscreen game on a second monitor), else the primary.
            var gameWindow = opts.AudioProcess is string game
                ? Process.GetProcessesByName(Path.GetFileNameWithoutExtension(game)).FirstOrDefault(p => p.MainWindowHandle != IntPtr.Zero)
                : null;
            monitor = gameWindow is null ? CaptureInterop.PrimaryMonitor() : CaptureInterop.MonitorOf(gameWindow.MainWindowHandle);
            item = CaptureInterop.ItemForMonitor(monitor);
            source = gameWindow is null ? "monitor:primary" : $"monitor:{opts.AudioProcess}";
        }

        int frameWidth = item.Size.Width, frameHeight = item.Size.Height;
        int cropX = 0, cropY = 0, width = frameWidth, height = frameHeight;
        var (screenWidth, screenHeight) = CaptureInterop.MonitorSize(monitor);
        Directory.CreateDirectory(opts.OutDir);

        D3D11.D3D11CreateDevice(null, DriverType.Hardware, DeviceCreationFlags.BgraSupport | DeviceCreationFlags.VideoSupport, null, out ID3D11Device? device)
            .CheckError();
        using var d3d = device!;
        var context = d3d.ImmediateContext;
        var winRtDevice = CaptureInterop.WinRtDevice(d3d);
        var framePool = Direct3D11CaptureFramePool.CreateFreeThreaded(
            winRtDevice, DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, item.Size);
        var session = framePool.CreateCaptureSession(item);
        session.IsCursorCaptureEnabled = opts.Cursor;
        try { session.IsBorderRequired = false; } catch (Exception) { /* not available before Windows 11 */ }

        FfmpegEncoder? encoder = null;
        using var frameLog = new VideoFrameLogWriter(new StreamWriter(Path.Combine(opts.OutDir, "video_frames.csv")));
        var resampler = new GridResampler(opts.Fps);

        var gate = new object();
        var stop = new ManualResetEventSlim();
        string? error = null;
        bool stopped = false;
        FrameWriter? writer = null;
        byte[]? shown = null;   // frame currently on screen (last captured)
        Nv12Converter? converter = null;
        (ID3D11Texture2D Staging, double T)? pending = null;   // converted, read when the next frame arrives
        AudioLoopback? audio = null;
        string? audioError = null, audioFallback = null;

        // The video size is fixed by the first frame, not item.Size: a windowed game's item can be larger than its frames
        // (1296x759 item, 1282x752 frames, seen on Arma Reforger), so the encoder starts once the first frame is in.
        // A window is cropped to its client area: the video is the game's picture, without title bar and borders.
        Nv12Converter StartConverter()
        {
            (cropX, cropY, width, height) = window == IntPtr.Zero
                ? (0, 0, frameWidth, frameHeight)
                : CaptureInterop.ClientArea(window, frameWidth, frameHeight);
            converter = new Nv12Converter(d3d, context, cropX, cropY, width, height);
            (width, height) = (converter.Width, converter.Height);
            return converter;
        }
        FfmpegEncoder StartEncoder()
        {
            var nv12 = StartConverter();
            var started = new FfmpegEncoder(opts.Ffmpeg, Path.Combine(opts.OutDir, "video.mp4"), width, height, opts.Fps,
                encoderName.Result, audio?.Input);
            writer = new FrameWriter(started, frameLog, nv12.FrameBytes);
            shown = writer.Rent();
            return started;
        }
        double startQpcMs = QpcClock.NowMs();
        var startUtc = DateTimeOffset.UtcNow;
        double? firstFrameLatencyMs = null;
        if (opts.Audio)
        {
            if (opts.AudioProcess is string audioProcess && gameProcessId is null)
            {
                var candidates = Process.GetProcessesByName(Path.GetFileNameWithoutExtension(audioProcess));
                gameProcessId = (candidates.FirstOrDefault(p => p.MainWindowHandle != IntPtr.Zero) ?? candidates.FirstOrDefault())?.Id;
            }
            // The game's own sound when its process is known and Windows can single it out; else the whole playback
            // device (voice chat and other apps included), with the reason kept; else no audio, with the reason kept.
            if (gameProcessId is int pid)
            {
                try
                {
                    audio = new AudioLoopback(pid);
                }
                catch (Exception e)
                {
                    audioFallback = e.Message;
                    Console.Error.WriteLine($"game audio not available, recording the playback device: {e.Message}");
                }
            }
            if (audio is null)
            {
                try
                {
                    audio = new AudioLoopback();
                }
                catch (Exception e)
                {
                    audioError = audioFallback is null ? e.Message : $"{audioFallback}; playback device: {e.Message}";
                    Console.Error.WriteLine($"audio not recorded: {e.Message}");
                }
            }
            if (!opts.Probe) audio?.Start();  // a probe only asks where the sound would come from
        }
        AudioStatus? AudioNow() => !opts.Audio ? null : audio?.Status(audioFallback) ?? new AudioStatus(AudioStatus.NotRecorded, Error: audioError);

        // The ticks show the frame on screen until now; next (null at stop) replaces it.
        void Emit(IReadOnlyList<GridTick> ticks, byte[]? next)
        {
            if (ticks is [{ Index: 0 } first, ..]) audio?.SetOrigin(first.TGridMs);
            writer!.Submit(shown!, ticks);
            shown = next;
        }

        long starvedFrames = 0;
        void ShowPending()
        {
            if (pending is not { } p) return;
            pending = null;
            // Runs under the gate, which the stop path takes: when ffmpeg is a whole pool behind, this frame goes and the
            // one on screen stands in (duplicated by the grid), rather than everything waiting on ffmpeg.
            if (!writer!.TryRent(RentWait, out var incoming))
            {
                starvedFrames++;
                return;
            }
            converter!.Read(p.Staging, incoming);
            Emit(resampler.Advance(p.T), incoming);
        }

        long sizeChangedFrames = 0;
        int probeFrames = 0;
        bool probeBlack = true;
        double probeLuma = 0;
        // Converts the frame and looks at its luma: all under 32 (16 is black in the limited range) is a black frame.
        bool Probe(Direct3D11CaptureFrame frame)
        {
            var nv12 = converter ??= StartConverter();
            using var texture = CaptureInterop.Texture(frame.Surface);
            var buffer = new byte[nv12.FrameBytes];
            nv12.Read(nv12.Convert(texture), buffer);
            var luma = buffer.AsSpan(0, nv12.Width * nv12.Height);
            long sum = 0;
            byte max = 0;
            foreach (byte y in luma) { sum += y; if (y > max) max = y; }
            probeLuma += (double)sum / luma.Length;
            probeBlack &= max < 32;
            return ++probeFrames >= 10;
        }

        framePool.FrameArrived += (pool, _) =>
        {
            lock (gate)
            {
                using var frame = pool.TryGetNextFrame();
                if (stopped || frame is null) return;
                if (encoder is null && (frame.ContentSize.Width != frameWidth || frame.ContentSize.Height != frameHeight))
                {
                    (frameWidth, frameHeight) = (frame.ContentSize.Width, frame.ContentSize.Height);
                    framePool.Recreate(winRtDevice, DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, frame.ContentSize);
                    return;
                }
                if (opts.Probe)
                {
                    if (Probe(frame)) stop.Set();
                    return;
                }
                encoder ??= StartEncoder();
                if (frame.ContentSize.Width != frameWidth || frame.ContentSize.Height != frameHeight)
                {
                    // The game changed size or mode (alt-tab out of fullscreen): its frames no longer fit the video, so the
                    // last good frame stays on screen (GridResampler duplicates it) until the size is back or the stop.
                    sizeChangedFrames++;
                    return;
                }
                if (writer!.Error is string writeError)
                {
                    error = writeError;
                    stopped = true;
                    stop.Set();
                    return;
                }
                double t = frame.SystemRelativeTime.TotalMilliseconds;  // QPC-based, same clock as QpcClock.NowMs()
                firstFrameLatencyMs ??= QpcClock.NowMs() - t;

                using var texture = CaptureInterop.Texture(frame.Surface);
                var staging = converter!.Convert(texture);
                ShowPending();
                pending = (staging, t);
            }
        };
        item.Closed += (_, _) => stop.Set();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.Set(); };
        if (opts.StopEvent is string stopEventName)
        {
            var stopEvent = EventWaitHandle.OpenExisting(stopEventName);
            _ = Task.Run(() => { stopEvent.WaitOne(); stop.Set(); });
        }

        session.StartCapture();
        if (opts.Probe)
        {
            stop.Wait(TimeSpan.FromSeconds(2));
            lock (gate) stopped = true;
            session.Dispose();
            framePool.Dispose();
            converter?.Dispose();
            Console.WriteLine(new CaptureProbe(width, height, probeFrames, probeFrames > 0 && probeBlack,
                probeFrames > 0 ? Math.Round(probeLuma / probeFrames, 1) : 0,
                window == IntPtr.Zero ? null : CaptureInterop.WindowStyle(window, monitor), source, AudioNow()).ToJson());
            audio?.Dispose();
            return 0;
        }
        if (opts.JsonStatus)
            Console.WriteLine(new RecorderStatus(RecorderStatus.Started, Width: width, Height: height, Fps: opts.Fps, Audio: AudioNow()).ToJson());
        else
            Console.WriteLine($"recording {width}x{height} @ {opts.Fps} fps to {opts.OutDir} (Ctrl+C to stop)");
        using (var progress = opts.JsonStatus ? new Timer(_ =>
               {
                   RecorderStatus s;
                   lock (gate) s = new(RecorderStatus.Progress, writer?.Written ?? 0, resampler.Duplicates, resampler.DroppedSourceFrames);
                   Console.WriteLine(s.ToJson());
               }, null, 1000, 1000) : null)
        {
            if (opts.Seconds is double seconds) stop.Wait(TimeSpan.FromSeconds(seconds)); else stop.Wait();
        }

        double stopQpcMs = QpcClock.NowMs();
        var phase = Stopwatch.StartNew();
        lock (gate)
        {
            encoder ??= StartEncoder();
            if (!stopped)
            {
                stopped = true;
                ShowPending();
                Emit(resampler.Finish(stopQpcMs), null);
            }
        }
        writer!.Complete(DrainTimeout);
        error ??= writer.Error;
        long framesWritten = writer.Written;
        long drainMs = phase.ElapsedMilliseconds;
        frameLog.Flush();
        session.Dispose();
        framePool.Dispose();
        phase.Restart();
        if (audio is not null)
        {
            // Video ends first: ffmpeg stops reading the audio pipe while it waits for video, so audio's final padding would
            // block forever. Both inputs must end before encoder.Finish waits for ffmpeg to exit.
            encoder.EndVideo();
            audio.Finish(stopQpcMs);
            if (audio.Error is not null) Console.Error.WriteLine($"audio: {audio.Error}");
        }
        long audioMs = phase.ElapsedMilliseconds;
        var audioStatus = AudioNow();
        phase.Restart();
        int ffmpegExit = encoder.Finish(FfmpegExitTimeout);
        var stopTiming = new StopTiming(drainMs, audioMs, phase.ElapsedMilliseconds, FfmpegKilled: ffmpegExit == -1);
        if (ffmpegExit == -1) error ??= $"ffmpeg did not exit after the end of its input (encoder {encoder.Encoder}); killed";
        encoder.Dispose();
        audio?.Dispose();
        converter?.Dispose();

        var meta = new Dictionary<string, object?>
        {
            ["capture_id"] = Guid.NewGuid().ToString("N"),
            ["tool_version"] = BuildInfo.Version,
            ["width"] = width,
            ["height"] = height,
            ["screen_width"] = screenWidth,
            ["screen_height"] = screenHeight,
            ["fps"] = opts.Fps,
            ["encoder"] = encoderName.Result,
            ["source"] = source,
            ["cursor"] = opts.Cursor,
            ["started_utc"] = startUtc.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
            ["started_qpc_ms"] = startQpcMs,
            ["stopped_qpc_ms"] = stopQpcMs,
            ["frames"] = framesWritten,
            ["duplicate_frames"] = resampler.Duplicates,
            ["dropped_source_frames"] = resampler.DroppedSourceFrames,
            ["size_changed_frames"] = sizeChangedFrames,
            ["starved_frames"] = starvedFrames,
            ["first_frame_capture_latency_ms"] = firstFrameLatencyMs,
            ["ffmpeg_exit_code"] = ffmpegExit,
            ["stop"] = stopTiming.ToNode(),
            ["error"] = error,
            ["audio"] = audioStatus is null ? null : new Dictionary<string, object?>
            {
                ["sample_rate"] = audio?.Input.SampleRate,
                ["channels"] = audio?.Input.Channels,
                ["start_qpc_ms"] = audio?.OriginMs,
                ["status"] = audioStatus.ToNode(),
            },
        };
        File.WriteAllText(Path.Combine(opts.OutDir, "recording.json"),
            JsonSerializer.Serialize(meta, new JsonSerializerOptions
            {
                WriteIndented = true,
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            }));

        if (error is null && ffmpegExit != 0) error = $"ffmpeg exited with code {ffmpegExit}";
        if (sizeChangedFrames > 0)
            Console.Error.WriteLine($"{sizeChangedFrames} captured frames had another size (the game changed mode); the last frame before stood in");
        if (opts.JsonStatus)
        {
            Console.WriteLine(new RecorderStatus(RecorderStatus.Stopped, framesWritten, resampler.Duplicates,
                resampler.DroppedSourceFrames, width, height, opts.Fps, error, audioStatus, stopTiming).ToJson());
        }
        else
        {
            Console.WriteLine($"stopped: {meta["frames"]} frames, {resampler.Duplicates} duplicated, " +
                              $"{resampler.DroppedSourceFrames} dropped, ffmpeg exit {ffmpegExit}");
        }
        if (error is not null) Console.Error.WriteLine(error);
        return error is null && ffmpegExit == 0 ? 0 : 1;
    }

    private static int RunObs(Options opts, string address)
    {
        using var stop = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.Cancel(); };
        if (opts.StopEvent is string stopEventName)
        {
            var stopEvent = EventWaitHandle.OpenExisting(stopEventName);
            _ = Task.Run(() => { stopEvent.WaitOne(); stop.Cancel(); });
        }
        if (opts.Seconds is double seconds) stop.CancelAfter(TimeSpan.FromSeconds(seconds));
        void Status(RecorderStatus s)
        {
            if (opts.JsonStatus) Console.WriteLine(s.ToJson());
            else if (s.Event == RecorderStatus.Started) Console.WriteLine($"OBS recording {s.Width}x{s.Height} @ {s.Fps} fps (Ctrl+C to stop)");
            else if (s.Event == RecorderStatus.Stopped) Console.WriteLine($"stopped: {s.Frames} frames");
        }
        string? error;
        try
        {
            error = ObsRecorder.RunAsync(new ObsRecordOptions(address, Environment.GetEnvironmentVariable(ObsPasswordVariable),
                opts.OutDir, opts.Ffmpeg), Status, stop.Token).GetAwaiter().GetResult();
        }
        catch (Exception e) when (e is ObsException or TimeoutException)
        {
            error = e.Message;
            if (opts.JsonStatus) Console.WriteLine(new RecorderStatus(RecorderStatus.Stopped, Error: error).ToJson());
        }
        if (error is not null) Console.Error.WriteLine(error);
        return error is null ? 0 : 1;
    }

    public const string ObsPasswordVariable = "ODX_OBS_PASSWORD";
}

internal sealed record Options(string OutDir, string? Window, double Fps, string Encoder, string Ffmpeg, bool Cursor,
    bool Audio, string? AudioProcess, string? Obs, double? Seconds, string? StopEvent, bool JsonStatus, bool Probe)
{
    public static Options? Parse(string[] args)
    {
        string? outDir = null, window = null, encoder = "auto", ffmpeg = ExternalTools.Ffmpeg(), stopEvent = null, audioProcess = null, obs = null;
        bool monitor = false, cursor = false, audio = false, jsonStatus = false, probe = false;
        double fps = 60;
        double? seconds = null;
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        for (int i = 0; i < args.Length; i++)
        {
            string? Next() => i + 1 < args.Length ? args[++i] : null;
            switch (args[i])
            {
                case "--out": outDir = Next(); break;
                case "--window": window = Next(); break;
                case "--monitor": monitor = true; break;
                case "--fps" when double.TryParse(Next(), System.Globalization.NumberStyles.Float, inv, out var f): fps = f; break;
                case "--encoder": encoder = Next(); break;
                case "--ffmpeg": ffmpeg = Next(); break;
                case "--cursor": cursor = true; break;
                case "--audio": audio = true; break;
                case "--audio-process": audioProcess = Next(); break;
                case "--obs": obs = Next(); break;
                case "--seconds" when double.TryParse(Next(), System.Globalization.NumberStyles.Float, inv, out var s): seconds = s; break;
                case "--stop-event": stopEvent = Next(); break;
                case "--json-status": jsonStatus = true; break;
                case "--probe": probe = true; break;
                default: return null;
            }
        }
        if (outDir is null || encoder is null || ffmpeg is null || (window is null) == !monitor || fps <= 0) return null;
        return new Options(outDir, window, fps, encoder, ffmpeg, cursor, audio, audioProcess, obs, seconds, stopEvent, jsonStatus, probe);
    }
}
