// odx record --out <dir> (--window <process name> | --monitor) [--fps 60] [--encoder auto|x264|nvenc|mf] [--ffmpeg <path>]
//            [--cursor] [--audio [--audio-process <process name>]] [--obs <host:port>] [--seconds N] [--stop-event <name>]
//            [--grid-origin-qpc-ms <ms>] [--json-status] [--probe]
//
// Records a game window, or a monitor (the one the --audio-process game's window is on, else the primary), with
// Windows.Graphics.Capture. A window must be windowed or borderless: an exclusive-fullscreen window gives black frames,
// which --probe tells (captures up to 2 s, prints a CaptureProbe JSON line, records nothing). A window whose size
// changes while recording (alt-tab out of fullscreen) keeps the recording going on its last frame until the size is
// back (size_changed_frames in recording.json). Every captured frame carries its QPC
// capture time (SystemRelativeTime), the same clock as odx input. Frames are resampled onto a constant-rate grid
// (GridResampler) and piped to ffmpeg, so the video is CFR and its frame N is row N of video_frames.csv. The grid's
// tick 0 is the first captured frame, or --grid-origin-qpc-ms (a round's start on this machine's QPC clock: every
// player's frame N is then the same instant; frames before it are discarded, ticks before the first frame are black).
// Writes:
//   <dir>/video_raw.mp4     fragmented, video only, CFR H.264, no B-frames: survives a kill
//   <dir>/audio.wav         --audio: the game process's own sound (--audio-process, else the --window process; not voice
//                           chat, browsers or other apps), or everything the default playback device plays when no game
//                           process is known; sample 0 at video frame 0, nothing after the stop
//   <dir>/video_frames.csv  frame_index,t_grid_qpc_ms,t_source_qpc_ms,duplicate
//   <dir>/recording.json    capture id, capture and screen size, fps, encoder, start time (UTC + QPC), the grid origin,
//                           counts of duplicated/dropped/black/starved frames, where the time went (encoder, readback),
//                           audio (null without --audio: sample rate, channels, start, and a status: source
//                           process|device|none, fallback reason, timing, counters)
// The two media files become video.mp4 afterwards (VideoFinalize: the capture host, or odx build), not here: a round's
// video is cut to the round's end there, which this process knows nothing about.
// --obs: OBS records instead, with its own scene, output and audio settings (ObsRecorder); the WebSocket password, if
// any, comes from the ODX_OBS_PASSWORD environment variable. The same three files are written.
// Stop with Ctrl+C, --seconds, --stop-event (a named event set by Odx.App), or by closing the captured window. No pipe
// can hold the stop (ffmpeg gets video only, the sound goes to a file) and a kill never loses the round.
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
    // (StopTiming) rather than in the capture host killing this process and knowing nothing: the limits in a row (drain,
    // AudioLoopback.Finish, ffmpeg's exit) must add up to well under CaptureRun.StopTimeout (60 s).
    // A capture callback blocked here holds up WGC's 2-frame pool: short, so a slow encoder costs frames, not the capture
    // of what follows (Bảo's round of 2026-11-07 spent most of its 49 s in this wait).
    private static readonly TimeSpan RentWait = TimeSpan.FromMilliseconds(20);
    /// <summary>
    /// At the stop, for the capture callback to let go of the gate and for the GPU to hand over the last frame: past it
    /// the frame before stands in. 54 stops of 2026-11-08/09 (21 machines, 1 % of all) hung here for good.
    /// </summary>
    private static readonly TimeSpan LastFrameWait = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan DrainTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan FfmpegExitTimeout = TimeSpan.FromSeconds(20);
    /// <summary>After the stop signal: past the limits above (~47 s in all), a step that never returns ends the process (see the watchdog).</summary>
    private static readonly TimeSpan StopWatchdog = TimeSpan.FromSeconds(55);

    public static int Run(string[] args)
    {
        var opts = Options.Parse(args);
        if (opts is null)
        {
            Console.Error.WriteLine("usage: odx record --out <dir> (--window <process name> | --monitor) [--fps 60] " +
                                    "[--encoder auto|x264|nvenc|mf] [--ffmpeg <path>] [--cursor] [--audio [--audio-process <process name>]] [--obs <host:port>] [--source present|gfxcapture] [--seconds N] [--stop-event <name>] [--grid-origin-qpc-ms <ms>] [--json-status] [--probe]");
            return 2;
        }
        if (opts.Obs is string obs) return RunObs(opts, obs);
        string? presentFallback = null;
        if (opts.Source == "present")
        {
            var (code, fallback) = RunPresent(opts);
            if (code is int c) return c;
            presentFallback = fallback;  // the DLL produced no frame: Windows.Graphics.Capture records this round instead
            Console.Error.WriteLine($"present source unavailable, using Windows.Graphics.Capture: {fallback}");
        }
        if (opts.Source == "gfxcapture") return RunGfxCapture(opts);
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
        // Flushed per row (30 small writes a second): a recorder killed mid-way leaves every frame's row, not a 4 KB buffer short.
        using var frameLog = new VideoFrameLogWriter(new StreamWriter(Path.Combine(opts.OutDir, "video_frames.csv")) { AutoFlush = true });
        var resampler = new GridResampler(opts.Fps, opts.GridOriginQpcMs);

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
            var started = new FfmpegEncoder(opts.Ffmpeg, Path.Combine(opts.OutDir, VideoFinalize.RawVideo), width, height, opts.Fps,
                encoderName.Result);
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
                    audio = new AudioLoopback(pid, Path.Combine(opts.OutDir, VideoFinalize.AudioFile));
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
                    audio = new AudioLoopback(Path.Combine(opts.OutDir, VideoFinalize.AudioFile));
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
            if (ticks is [{ Black: true }, ..])
            {
                writer!.Submit(null, ticks);   // before the first frame: black, and the (empty) frame buffer goes back
                writer.Submit(shown!, []);
            }
            else writer!.Submit(shown!, ticks);
            shown = next;
        }

        long sourceFrames = 0, starvedFrames = 0, readbackMs = 0, readbackMaxMs = 0;
        string[] captureStep = ["idle"];   // what the capture callback is in, for a stop that waits on it
        double givenUpT = double.NegativeInfinity;   // capture time of the frame the stop gave up
        bool readbackAbandoned = false;   // the stop's Map never returned: its thread and buffer are left, D3D is not touched again
        // False when the GPU did not hand the frame over within readWait (the stop's bounded read).
        bool ShowPending(TimeSpan? readWait = null)
        {
            if (pending is not { } p) return true;
            pending = null;
            givenUpT = p.T;
            // Runs under the gate, which the stop path takes: when ffmpeg is a whole pool behind, this frame goes and the
            // one on screen stands in (duplicated by the grid), rather than everything waiting on ffmpeg.
            if (!writer!.TryRent(RentWait, out var incoming))
            {
                starvedFrames++;
                return true;
            }
            long started = Stopwatch.GetTimestamp();
            bool read;
            if (readWait is { } wait)
            {
                // The stop's readback, on its own thread: on 2026-11-09 (7 stops, 0.3.1) the Map itself never returned,
                // DoNotWait or not, with the callback idle; the frame is given up and that thread left where it is.
                var readback = Task.Run(() => converter!.Read(p.Staging, incoming, wait));
                try
                {
                    read = readback.Wait(wait + TimeSpan.FromSeconds(1)) && readback.Result;
                }
                catch (AggregateException)
                {
                    read = false;
                }
                if (!readback.IsCompleted) readbackAbandoned = true;
            }
            else read = converter!.Read(p.Staging, incoming);
            long ms = (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            readbackMs += ms;
            readbackMaxMs = Math.Max(readbackMaxMs, ms);
            if (read) Emit(resampler.Advance(p.T), incoming);
            else if (!readbackAbandoned) writer.Submit(incoming, []);   // back to the pool unused
            return read;
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
            try
            {
                lock (gate)
                {
                    Volatile.Write(ref captureStep[0], "next frame");
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
                    sourceFrames++;

                    Volatile.Write(ref captureStep[0], "convert");
                    using var texture = CaptureInterop.Texture(frame.Surface);
                    var staging = converter!.Convert(texture);
                    Volatile.Write(ref captureStep[0], "readback");
                    ShowPending();
                    pending = (staging, t);
                }
            }
            finally
            {
                Volatile.Write(ref captureStep[0], "idle");
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
                   var audioNow = AudioNow();
                   lock (gate) s = new(RecorderStatus.Progress, writer?.Written ?? 0, resampler.Duplicates, resampler.DroppedSourceFrames,
                       Starved: starvedFrames, LeadingBlack: resampler.LeadingBlackFrames, Audio: audioNow);
                   Console.WriteLine(s.ToJson());
               }, null, 1000, 1000) : null)
        {
            if (opts.Seconds is double seconds) stop.Wait(TimeSpan.FromSeconds(seconds)); else stop.Wait();
        }

        double stopQpcMs = QpcClock.NowMs();
        audio?.End(stopQpcMs);  // the sound ends here, whatever the video's drain takes
        var stopClock = Stopwatch.StartNew();
        var phase = Stopwatch.StartNew();
        string[] stopStep = ["capture gate"];
        bool[] drained = [false];
        string? stopNote = null;   // the last frame was given up, and why
        long tailFrames = 0;       // frames after the given-up one showing the frame before
        StopTiming? stopTiming = null;
        int? ffmpegExit = null;
        bool recordingWritten = false;   // recording.json and the stopped status are out: the watchdog adds nothing
        void Step(string name) => Volatile.Write(ref stopStep[0], name);

        Dictionary<string, object?> Meta(long frames, int? exit, StopTiming? timing, string? problem, AudioStatus? audioStatus) => new()
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
            ["present_fallback"] = presentFallback,
            ["cursor"] = opts.Cursor,
            ["started_utc"] = startUtc.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
            ["started_qpc_ms"] = startQpcMs,
            ["stopped_qpc_ms"] = stopQpcMs,
            ["grid_origin_qpc_ms"] = opts.GridOriginQpcMs,
            ["frames"] = frames,
            ["duplicate_frames"] = resampler.Duplicates,
            ["dropped_source_frames"] = resampler.DroppedSourceFrames,
            ["size_changed_frames"] = sizeChangedFrames,
            ["leading_black_frames"] = resampler.LeadingBlackFrames,
            ["source_frames"] = sourceFrames,
            ["starved_frames"] = starvedFrames,
            ["encode_write_ms"] = writer?.WriteMs,
            ["encode_write_max_ms"] = writer?.WriteMaxMs,
            ["readback_ms"] = readbackMs,
            ["readback_max_ms"] = readbackMaxMs,
            ["first_frame_capture_latency_ms"] = firstFrameLatencyMs,
            ["ffmpeg_exit_code"] = exit,
            ["stop"] = timing?.ToNode(),
            ["error"] = problem,
            ["audio"] = audioStatus is null ? null : new Dictionary<string, object?>
            {
                ["sample_rate"] = audio?.Input.SampleRate,
                ["channels"] = audio?.Input.Channels,
                ["start_qpc_ms"] = audio?.OriginMs,
                ["status"] = audioStatus.ToNode(),
            },
        };
        void WriteRecordingJson(Dictionary<string, object?> meta) => File.WriteAllText(Path.Combine(opts.OutDir, "recording.json"),
            JsonSerializer.Serialize(meta, new JsonSerializerOptions
            {
                WriteIndented = true,
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            }));

        // Watchdog: the limits below add up to ~47 s (gate 2, readback 3, drain 10, audio 7, ffmpeg 20 + 5 after a kill),
        // but a Windows call on the stop path can simply never return (WGC,
        // D3D or WASAPI teardown on the host PCs of 2026-11-06/07; which one, this names). Then the recording is ended
        // with what is on disk: recording.json and the stopped status say where it stuck, this process exits, ffmpeg
        // (a child with its own stdin pipe) sees the end of its input and finishes video_raw.mp4, and the capture host
        // finalizes it. The exit code is 0 when every frame had reached ffmpeg: the files are finished before the
        // capture is torn down, so a hang in the teardown costs nothing.
        using var watchdog = new Timer(_ =>
        {
            string where = Volatile.Read(ref stopStep[0]), callback = Volatile.Read(ref captureStep[0]);
            bool complete = Volatile.Read(ref drained[0]);
            long frames = writer?.Written ?? 0;
            string problem = $"stop stuck in '{where}' after {stopClock.Elapsed.TotalSeconds:0} s" +
                             (callback == "idle" ? "" : $" (capture callback in '{callback}')") +
                             "; ended with what is on disk" + (complete ? "" : " (frames may be missing)");
            Console.Error.WriteLine(problem);
            try
            {
                if (!recordingWritten) WriteRecordingJson(Meta(frames, ffmpegExit, stopTiming, problem, AudioNow()));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                Console.Error.WriteLine($"recording.json: {e.Message}");
            }
            if (opts.JsonStatus && !recordingWritten)
                Console.WriteLine(new RecorderStatus(RecorderStatus.Stopped, frames, resampler.Duplicates, resampler.DroppedSourceFrames,
                    width, height, opts.Fps, complete ? null : problem, Stop: stopTiming, Starved: starvedFrames,
                    LeadingBlack: resampler.LeadingBlackFrames).ToJson());
            Console.Out.Flush();
            Console.Error.Flush();
            Environment.Exit(complete && error is null ? 0 : 1);
        }, null, StopWatchdog, Timeout.InfiniteTimeSpan);

        // The last frame: the one the capture callback converted but has not read back, then the ticks up to the stop.
        // Neither the callback's gate nor the GPU readback is waited for past LastFrameWait: a stop hung in one of them
        // (54 rounds lost on 2026-11-08/09) now costs that frame, the frame before standing in until the stop.
        bool locked = Monitor.TryEnter(gate, LastFrameWait);
        if (!locked && encoder is null)
        {
            // The callback is inside StartEncoder: nothing is recorded yet, and a second encoder must not be started.
            Monitor.Enter(gate);
            locked = true;
        }
        try
        {
            if (!locked)
                stopNote = $"the capture callback did not let go within {LastFrameWait.TotalSeconds:0} s (in '{Volatile.Read(ref captureStep[0])}')";
            encoder ??= StartEncoder();
            if (!stopped)
            {
                Step("last frame readback");
                if (locked && !ShowPending(LastFrameWait))
                    stopNote = $"the GPU did not hand over the last frame within {LastFrameWait.TotalSeconds:0} s";
                stopped = true;
                Step("last ticks");
                var tail = resampler.Finish(stopQpcMs);
                // Ticks before the given-up frame's time show the frame before rightly; without the gate, the given-up frame is
                // unknown and the ticks since the last frame read back are counted.
                if (stopNote is not null) tailFrames = tail.Count(t => t.TGridMs >= givenUpT);
                Emit(tail, null);
            }
        }
        finally
        {
            if (locked) Monitor.Exit(gate);
        }
        if (stopNote is not null) Console.Error.WriteLine($"stop: {stopNote}; the last {tailFrames} frame(s) show the frame before");
        Step("drain");
        drained[0] = writer!.Complete(DrainTimeout);
        error ??= writer.Error;
        long framesWritten = writer.Written;
        long drainMs = phase.ElapsedMilliseconds;
        Step("frame log");
        frameLog.Flush();
        phase.Restart();
        Step("ffmpeg input");
        encoder.EndVideo();  // ffmpeg flushes the encoder while the audio file is padded and closed
        Step("audio");
        audio?.Finish(stopQpcMs);
        if (audio?.Error is { } audioProblem) Console.Error.WriteLine($"audio: {audioProblem}");
        long audioMs = phase.ElapsedMilliseconds;
        var audioStatus = AudioNow();
        phase.Restart();
        Step("ffmpeg exit");
        ffmpegExit = encoder.Finish(FfmpegExitTimeout);
        stopTiming = new StopTiming(drainMs, audioMs, phase.ElapsedMilliseconds, ffmpegExit == -1, stopNote, tailFrames);
        if (ffmpegExit == -1) error ??= $"ffmpeg did not exit after the end of its input (encoder {encoder.Encoder}); killed, the video keeps the fragments written before";
        Step("recording.json");
        var meta = Meta(framesWritten, ffmpegExit, stopTiming, error, audioStatus);
        WriteRecordingJson(meta);
        recordingWritten = true;
        if (error is null && ffmpegExit != 0) error = $"ffmpeg exited with code {ffmpegExit}";
        if (sizeChangedFrames > 0)
            Console.Error.WriteLine($"{sizeChangedFrames} captured frames had another size (the game changed mode); the last frame before stood in");
        if (error is not null) Console.Error.WriteLine(error);
        // The files are complete and the capture host has the outcome: what follows can hang without costing them.
        if (opts.JsonStatus)
        {
            Console.WriteLine(new RecorderStatus(RecorderStatus.Stopped, framesWritten, resampler.Duplicates,
                resampler.DroppedSourceFrames, width, height, opts.Fps, error, audioStatus, stopTiming, starvedFrames,
                resampler.LeadingBlackFrames).ToJson());
        }
        else
        {
            Console.WriteLine($"stopped: {meta["frames"]} frames, {resampler.Duplicates} duplicated, " +
                              $"{resampler.DroppedSourceFrames} dropped, ffmpeg exit {ffmpegExit}; " +
                              $"video_raw.mp4 and audio.wav become video.mp4 with odx build");
        }
        Console.Out.Flush();
        Step("ffmpeg dispose");
        encoder.Dispose();
        Step("audio dispose");
        audio?.Dispose();
        if (locked && !readbackAbandoned)
        {
            // A callback that never let go, or a readback that never returned, may still be inside WGC or D3D: these are
            // left to the process exit then.
            Step("capture session");
            session.Dispose();
            Step("frame pool");
            framePool.Dispose();
            Step("converter dispose");
            converter?.Dispose();
        }
        watchdog.Dispose();
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

    // --source present: NV12 frames straight from the game's own Present (odx_capture_x64's hook), paced to the capture
    // grid, so each video frame is a fresh game frame (research/arma3-present-capture §4). Needs --window (the game whose
    // Present is hooked). Returns the exit code, or (null, reason) to fall back to WGC when the DLL wrote no frame in 2 s.
    private static (int? Code, string? Fallback) RunPresent(Options opts)
    {
        if (opts.Window is not string processName)
        {
            Console.Error.WriteLine("--source present needs --window <game process>");
            return (2, null);
        }
        var proc = Process.GetProcessesByName(Path.GetFileNameWithoutExtension(processName))
            .FirstOrDefault(p => p.MainWindowHandle != IntPtr.Zero);
        if (proc is null)
        {
            Console.Error.WriteLine($"no window found for process '{processName}'");
            return (1, null);
        }
        int pid = proc.Id;
        var (screenWidth, screenHeight) = CaptureInterop.MonitorSize(CaptureInterop.MonitorOf(proc.MainWindowHandle));
        Directory.CreateDirectory(opts.OutDir);

        // The limiter in the DLL and this resampler share the grid origin; QPC is the same clock in both processes.
        double originMs = opts.GridOriginQpcMs ?? QpcClock.NowMs();
        using var mapping = PresentMapping.Create(pid, (int)opts.Fps, originMs);
        using var frameEvent = new EventWaitHandle(false, EventResetMode.AutoReset, PresentMapping.EventName(pid));
        mapping.Capture = 1;

        var stop = new ManualResetEventSlim();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.Set(); };
        if (opts.StopEvent is string stopEventName)
        {
            var stopEvent = EventWaitHandle.OpenExisting(stopEventName);
            _ = Task.Run(() => { stopEvent.WaitOne(); stop.Set(); });
        }
        if (opts.Seconds is double seconds)
            _ = Task.Run(() => { Thread.Sleep(TimeSpan.FromSeconds(seconds)); stop.Set(); });
        _ = Task.Run(() => { try { proc.WaitForExit(); } catch { /* already gone */ } stop.Set(); });

        // The DLL installs its hook on the first callExtension and only captures while it sees the mapping: give it 2 s.
        var wait = Stopwatch.StartNew();
        while (mapping.Width == 0)
        {
            if (stop.IsSet) { mapping.Capture = 0; return (0, null); }
            if (wait.Elapsed > TimeSpan.FromSeconds(2))
            {
                mapping.Capture = 0;
                return (null, $"odx_capture wrote no frame within 2 s (pid {pid})");
            }
            frameEvent.WaitOne(100);
        }

        int width = mapping.Width, height = mapping.Height;
        if (width > PresentMapping.MaxWidth || height > PresentMapping.MaxHeight)
        {
            mapping.Capture = 0;
            return (null, $"backbuffer {width}x{height} is larger than the present contract's {PresentMapping.MaxWidth}x{PresentMapping.MaxHeight}");
        }
        int frameBytes = width * height * 3 / 2;
        string encoder = EncoderChoice.Resolve(opts.Ffmpeg, opts.Encoder);
        string source = $"present:{Path.GetFileNameWithoutExtension(processName)}";

        using var frameLog = new VideoFrameLogWriter(new StreamWriter(Path.Combine(opts.OutDir, "video_frames.csv")) { AutoFlush = true });
        var resampler = new GridResampler(opts.Fps, originMs);
        var encoderProc = new FfmpegEncoder(opts.Ffmpeg, Path.Combine(opts.OutDir, VideoFinalize.RawVideo), width, height, opts.Fps, encoder);
        var writer = new FrameWriter(encoderProc, frameLog, frameBytes);

        double startQpcMs = QpcClock.NowMs();
        var startUtc = DateTimeOffset.UtcNow;

        AudioLoopback? audio = null;
        string? audioError = null, audioFallback = null;
        if (opts.Audio)
        {
            try { audio = new AudioLoopback(pid, Path.Combine(opts.OutDir, VideoFinalize.AudioFile)); }
            catch (Exception e)
            {
                audioFallback = e.Message;
                try { audio = new AudioLoopback(Path.Combine(opts.OutDir, VideoFinalize.AudioFile)); }
                catch (Exception e2) { audioError = $"{e.Message}; playback device: {e2.Message}"; }
            }
            audio?.Start();
        }
        AudioStatus? AudioNow() => !opts.Audio ? null : audio?.Status(audioFallback) ?? new AudioStatus(AudioStatus.NotRecorded, Error: audioError);

        long sourceFrames = 0, starvedFrames = 0;
        double lastPresentMs = double.NegativeInfinity;
        byte[]? shown = null;   // the frame GridResampler currently shows; the next frame's Advance emits it
        bool originSet = false;

        // The ticks show the frame on screen (shown) up to now; next takes its place. Black ticks before the first frame
        // submit a null frame (FrameWriter writes NV12 black).
        void Emit(IReadOnlyList<GridTick> ticks, byte[]? next)
        {
            if (!originSet && ticks is [{ Index: 0 } first, ..]) { audio?.SetOrigin(first.TGridMs); originSet = true; }
            writer.Submit(shown, ticks);
            shown = next;
        }

        if (opts.JsonStatus)
            Console.WriteLine(new RecorderStatus(RecorderStatus.Started, Width: width, Height: height, Fps: opts.Fps, Audio: AudioNow()).ToJson());
        else
            Console.WriteLine($"recording {width}x{height} @ {opts.Fps} fps from the game's Present to {opts.OutDir} (Ctrl+C to stop)");

        using (var progress = opts.JsonStatus ? new Timer(_ =>
               Console.WriteLine(new RecorderStatus(RecorderStatus.Progress, writer.Written, resampler.Duplicates,
                   resampler.DroppedSourceFrames, Starved: starvedFrames, LeadingBlack: resampler.LeadingBlackFrames,
                   Audio: AudioNow()).ToJson()), null, 1000, 1000) : null)
        {
            while (!stop.IsSet)
            {
                frameEvent.WaitOne(200);
                if (writer.Error is not null) break;
                if (!writer.TryRent(RentWait, out var incoming)) { starvedFrames++; continue; }
                if (mapping.TryReadNewest(incoming, out double presentMs) && presentMs > lastPresentMs)
                {
                    lastPresentMs = presentMs;
                    sourceFrames++;
                    Emit(resampler.Advance(presentMs), incoming);
                }
                else writer.Submit(incoming, []);  // no new frame since the last read (or a torn read): buffer back to the pool
            }
        }

        double stopQpcMs = QpcClock.NowMs();
        mapping.Capture = 0;
        audio?.End(stopQpcMs);
        Emit(resampler.Finish(stopQpcMs), null);
        bool drained = writer.Complete(DrainTimeout);
        string? error = writer.Error;
        long framesWritten = writer.Written;
        frameLog.Flush();
        encoderProc.EndVideo();
        audio?.Finish(stopQpcMs);
        if (audio?.Error is { } audioProblem) Console.Error.WriteLine($"audio: {audioProblem}");
        var audioStatus = AudioNow();
        int ffmpegExit = encoderProc.Finish(FfmpegExitTimeout);
        if (!drained) error ??= "ffmpeg stopped reading video";
        if (error is null && ffmpegExit != 0) error = $"ffmpeg exited with code {ffmpegExit}";

        var meta = new Dictionary<string, object?>
        {
            ["capture_id"] = Guid.NewGuid().ToString("N"),
            ["tool_version"] = BuildInfo.Version,
            ["width"] = width,
            ["height"] = height,
            ["screen_width"] = screenWidth,
            ["screen_height"] = screenHeight,
            ["fps"] = opts.Fps,
            ["encoder"] = encoder,
            ["source"] = source,
            ["cursor"] = opts.Cursor,
            ["started_utc"] = startUtc.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
            ["started_qpc_ms"] = startQpcMs,
            ["stopped_qpc_ms"] = stopQpcMs,
            ["grid_origin_qpc_ms"] = opts.GridOriginQpcMs,
            ["frames"] = framesWritten,
            ["duplicate_frames"] = resampler.Duplicates,
            ["dropped_source_frames"] = resampler.DroppedSourceFrames,
            ["leading_black_frames"] = resampler.LeadingBlackFrames,
            ["source_frames"] = sourceFrames,
            ["starved_frames"] = starvedFrames,
            ["encode_write_ms"] = writer.WriteMs,
            ["encode_write_max_ms"] = writer.WriteMaxMs,
            ["ffmpeg_exit_code"] = ffmpegExit,
            ["error"] = error,
            ["audio"] = audioStatus is null ? null : new Dictionary<string, object?>
            {
                ["sample_rate"] = audio?.Input.SampleRate,
                ["channels"] = audio?.Input.Channels,
                ["start_qpc_ms"] = audio?.OriginMs,
                ["status"] = audioStatus.ToNode(),
            },
        };
        File.WriteAllText(Path.Combine(opts.OutDir, "recording.json"), JsonSerializer.Serialize(meta,
            new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));

        if (opts.JsonStatus)
            Console.WriteLine(new RecorderStatus(RecorderStatus.Stopped, framesWritten, resampler.Duplicates,
                resampler.DroppedSourceFrames, width, height, opts.Fps, error, audioStatus, Starved: starvedFrames,
                LeadingBlack: resampler.LeadingBlackFrames).ToJson());
        else
            Console.WriteLine($"stopped: {framesWritten} frames, {resampler.Duplicates} duplicated, {resampler.DroppedSourceFrames} dropped, ffmpeg exit {ffmpegExit}");
        encoderProc.Dispose();
        audio?.Dispose();
        return (error is null && ffmpegExit == 0 ? 0 : 1, null);
    }

    // --source gfxcapture: ffmpeg captures the window on the GPU (gfxcapture, d3d11) and encodes it, so this process does no
    // readback. Free-running (no grid origin): video_frames.csv is a plain CFR grid from the start; round-aligned capture is
    // a later step (research/arma3-present-capture). Opt-in, separate from the default Windows.Graphics.Capture path.
    private static int RunGfxCapture(Options opts)
    {
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        if (opts.Window is not string processName)
        {
            Console.Error.WriteLine("--source gfxcapture needs --window <game process>");
            return 2;
        }
        var proc = Process.GetProcessesByName(Path.GetFileNameWithoutExtension(processName))
            .FirstOrDefault(p => p.MainWindowHandle != IntPtr.Zero);
        if (proc is null)
        {
            Console.Error.WriteLine($"no window found for process '{processName}'");
            return 1;
        }
        ulong hwnd = (ulong)proc.MainWindowHandle.ToInt64();
        int pid = proc.Id;
        var (screenWidth, screenHeight) = CaptureInterop.MonitorSize(CaptureInterop.MonitorOf(proc.MainWindowHandle));
        Directory.CreateDirectory(opts.OutDir);
        string encoder = EncoderChoice.Resolve(opts.Ffmpeg, opts.Encoder);
        string source = $"gfxcapture:{Path.GetFileNameWithoutExtension(processName)}";
        string raw = Path.Combine(opts.OutDir, VideoFinalize.RawVideo);
        string fr = opts.Fps.ToString(inv);
        int cursor = opts.Cursor ? 1 : 0;

        // gfxcapture frames are d3d11 BGRA: download, convert to BT.601 limited NV12 (matching the rest of the pipeline), encode.
        // width/height=-2: round the captured size down to an even number (H.264 needs even dimensions).
        string filter = $"gfxcapture=hwnd={hwnd}:capture_cursor={cursor}:max_framerate={fr}:width=-2:height=-2," +
                        "hwdownload,format=bgra,scale=out_range=tv:out_color_matrix=smpte170m,format=nv12";
        string tArg = opts.Seconds is double secs ? $"-t {secs.ToString(inv)} " : "";
        // -progress on stdout: out_time_us lets us place the first frame on the QPC clock (now - elapsed output time),
        // which the grid origin aligns against. -stats_period 0.1 gets an early first block.
        string args = $"-y -loglevel error -progress pipe:1 -stats_period 0.1 -init_hw_device d3d11va " +
                      $"-filter_complex \"{filter}\" -fps_mode cfr -r {fr} " +
                      $"{FfmpegEncoder.CodecArgs(encoder)} {FfmpegEncoder.ColorArgs} {tArg}" +
                      $"-movflags +frag_keyframe+empty_moov+default_base_moof -f mp4 \"{raw}\"";

        double startQpcMs = QpcClock.NowMs();
        var startUtc = DateTimeOffset.UtcNow;
        AudioLoopback? audio = null;
        string? audioError = null, audioFallback = null;
        if (opts.Audio)
        {
            try { audio = new AudioLoopback(pid, Path.Combine(opts.OutDir, VideoFinalize.AudioFile)); }
            catch (Exception e)
            {
                audioFallback = e.Message;
                try { audio = new AudioLoopback(Path.Combine(opts.OutDir, VideoFinalize.AudioFile)); }
                catch (Exception e2) { audioError = $"{e.Message}; playback device: {e2.Message}"; }
            }
            audio?.Start();
            audio?.SetOrigin(startQpcMs);
        }
        AudioStatus? AudioNow() => !opts.Audio ? null : audio?.Status(audioFallback) ?? new AudioStatus(AudioStatus.NotRecorded, Error: audioError);

        var ff = Process.Start(new ProcessStartInfo(opts.Ffmpeg, args)
        {
            UseShellExecute = false, RedirectStandardInput = true, RedirectStandardError = true, RedirectStandardOutput = true,
        }) ?? throw new InvalidOperationException("cannot start ffmpeg");
        var errTask = ff.StandardError.ReadToEndAsync();
        // The QPC of output frame 0: when a -progress block arrives, now - out_time_us is when frame 0 was captured
        // (corrects for frames already buffered). First reading wins.
        double firstFrameQpcMs = double.NaN;
        var progressTask = Task.Run(() =>
        {
            string? line;
            long frame = 0, outUs = -1;
            while ((line = ff.StandardOutput.ReadLine()) is not null)
            {
                int eq = line.IndexOf('=');
                if (eq <= 0) continue;
                string key = line[..eq], val = line[(eq + 1)..];
                if (key == "frame") long.TryParse(val, out frame);
                else if (key == "out_time_us") { if (!long.TryParse(val, out outUs)) outUs = -1; }
                else if (key == "progress" && double.IsNaN(firstFrameQpcMs) && frame >= 1 && outUs >= 0)
                    firstFrameQpcMs = QpcClock.NowMs() - outUs / 1000.0;
            }
        });
        void GracefulStop() { try { ff.StandardInput.Write('q'); ff.StandardInput.Flush(); } catch { /* already exiting */ } }
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; GracefulStop(); };
        if (opts.StopEvent is string stopEventName)
        {
            var stopEvent = EventWaitHandle.OpenExisting(stopEventName);
            _ = Task.Run(() => { stopEvent.WaitOne(); GracefulStop(); });
        }
        _ = Task.Run(() => { try { proc.WaitForExit(); } catch { /* gone */ } GracefulStop(); });  // the game closed

        if (opts.JsonStatus)
            Console.WriteLine(new RecorderStatus(RecorderStatus.Started, Fps: opts.Fps, Audio: AudioNow()).ToJson());
        else
            Console.WriteLine($"recording the window of {processName} with gfxcapture @ {opts.Fps} fps to {opts.OutDir} (Ctrl+C to stop)");

        ff.WaitForExit();
        try { progressTask.Wait(TimeSpan.FromSeconds(2)); } catch { /* stdout drained on exit */ }
        double stopQpcMs = QpcClock.NowMs();
        audio?.End(stopQpcMs);
        string ffErr = errTask.GetAwaiter().GetResult().Trim();
        string? error = ff.ExitCode == 0 ? null : $"ffmpeg exited with {ff.ExitCode}" + (ffErr.Length > 0 ? $": {ffErr}" : "");

        long frames = 0;
        int width = 0, height = 0;
        if (File.Exists(raw) && ff.ExitCode == 0)
        {
            try
            {
                var info = Mp4Reader.Read(raw);          // width/height from the track header (also valid for fragmented mp4)
                width = info.Width; height = info.Height;
                frames = VideoTrim.DecodedFrameCount(opts.Ffmpeg, raw);  // Mp4Reader counts the moov, which is empty for this fragmented file
            }
            catch (Exception e) when (e is InvalidDataException or IOException or InvalidOperationException) { error ??= $"frame count failed: {e.Message}"; }
        }

        // Each captured frame k is at t0 + k/fps (CFR, t0 = frame 0's QPC). With --grid-origin-qpc-ms (a round) each frame
        // is mapped to its nearest grid tick so frame N lines up with the other players; free-running puts the origin at t0.
        // phaseOffsetMs is how far t0 sits from a grid tick: the residual the ffmpeg-owned loop cannot resample away (the
        // measurement for whether gfxcapture can sync a round).
        double period = 1000.0 / opts.Fps;
        double t0 = double.IsNaN(firstFrameQpcMs) ? startQpcMs : firstFrameQpcMs;
        double gridOrigin = opts.GridOriginQpcMs ?? t0;
        double phaseOffsetMs = (t0 - gridOrigin) - Math.Round((t0 - gridOrigin) / period) * period;
        using (var frameLog = new VideoFrameLogWriter(new StreamWriter(Path.Combine(opts.OutDir, "video_frames.csv"))))
        {
            for (long k = 0; k < frames; k++)
            {
                double tsrc = t0 + k * period;
                long tick = (long)Math.Round((tsrc - gridOrigin) / period);
                frameLog.Write(new GridTick(k, gridOrigin + tick * period, tsrc, false));
            }
        }

        audio?.Finish(stopQpcMs);
        if (audio?.Error is { } audioProblem) Console.Error.WriteLine($"audio: {audioProblem}");
        var audioStatus = AudioNow();
        var meta = new Dictionary<string, object?>
        {
            ["capture_id"] = Guid.NewGuid().ToString("N"),
            ["tool_version"] = BuildInfo.Version,
            ["width"] = width,
            ["height"] = height,
            ["screen_width"] = screenWidth,
            ["screen_height"] = screenHeight,
            ["fps"] = opts.Fps,
            ["encoder"] = encoder,
            ["source"] = source,
            ["cursor"] = opts.Cursor,
            ["started_utc"] = startUtc.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
            ["started_qpc_ms"] = startQpcMs,
            ["stopped_qpc_ms"] = stopQpcMs,
            ["grid_origin_qpc_ms"] = opts.GridOriginQpcMs,
            ["first_frame_qpc_ms"] = double.IsNaN(firstFrameQpcMs) ? (double?)null : firstFrameQpcMs,
            ["grid_phase_offset_ms"] = Math.Round(phaseOffsetMs, 3),  // residual misalignment of frame 0 vs the grid (sync measurement)
            ["frames"] = frames,
            ["source_frames"] = frames,
            ["ffmpeg_exit_code"] = ff.ExitCode,
            ["error"] = error,
            ["audio"] = audioStatus is null ? null : new Dictionary<string, object?>
            {
                ["sample_rate"] = audio?.Input.SampleRate,
                ["channels"] = audio?.Input.Channels,
                ["start_qpc_ms"] = audio?.OriginMs,
                ["status"] = audioStatus.ToNode(),
            },
        };
        File.WriteAllText(Path.Combine(opts.OutDir, "recording.json"), JsonSerializer.Serialize(meta,
            new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));

        if (opts.JsonStatus)
            Console.WriteLine(new RecorderStatus(RecorderStatus.Stopped, frames, Width: width, Height: height, Fps: opts.Fps, Error: error, Audio: audioStatus).ToJson());
        else
            Console.WriteLine($"stopped: {frames} frames{(error is null ? "" : $"; {error}")}");
        audio?.Dispose();
        return error is null ? 0 : 1;
    }

    public const string ObsPasswordVariable = "ODX_OBS_PASSWORD";
}

/// <param name="GridOriginQpcMs">The grid's tick 0 on the QPC clock; null puts it at the first captured frame.</param>
internal sealed record Options(string OutDir, string? Window, double Fps, string Encoder, string Ffmpeg, bool Cursor,
    bool Audio, string? AudioProcess, string? Obs, double? Seconds, string? StopEvent, bool JsonStatus, bool Probe,
    double? GridOriginQpcMs = null, string? Source = null)
{
    public static Options? Parse(string[] args)
    {
        string? outDir = null, window = null, encoder = "auto", ffmpeg = ExternalTools.Ffmpeg(), stopEvent = null, audioProcess = null, obs = null, source = null;
        bool monitor = false, cursor = false, audio = false, jsonStatus = false, probe = false;
        double fps = 60;
        double? seconds = null, gridOrigin = null;
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
                case "--grid-origin-qpc-ms" when double.TryParse(Next(), System.Globalization.NumberStyles.Float, inv, out var g): gridOrigin = g; break;
                case "--json-status": jsonStatus = true; break;
                case "--probe": probe = true; break;
                case "--source": source = Next(); break;
                default: return null;
            }
        }
        if (outDir is null || encoder is null || ffmpeg is null || (window is null) == !monitor || fps <= 0) return null;
        if (source is not null && source is not ("present" or "gfxcapture")) return null;   // WGC is the default, unnamed source
        return new Options(outDir, window, fps, encoder, ffmpeg, cursor, audio, audioProcess, obs, seconds, stopEvent, jsonStatus, probe, gridOrigin, source);
    }
}
