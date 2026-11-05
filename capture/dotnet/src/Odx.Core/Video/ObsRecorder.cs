using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Odx.Core;

/// <param name="Address">host:port of OBS's WebSocket server.</param>
/// <param name="OutDir">Gets video.mp4, video_frames.csv and recording.json, as from the Windows.Graphics.Capture recorder.</param>
public sealed record ObsRecordOptions(string Address, string? Password, string OutDir, string Ffmpeg);

/// <summary>
/// odx record --obs: OBS records with the player's own scene and output settings; odx record starts and stops it over
/// obs-websocket and puts its file on the QPC clock. The file's first frame is the one OBS renders when StartRecord
/// arrives, frame k at t0 + k/fps, so t0 is the StartRecord round trip's midpoint. Measured with screen flashes
/// (plan "Timing model of odx-record"): within 0–20 ms of it. GetRecordStatus.outputDuration is not used for t0: it
/// starts counting ~1.1 s later, once the output is up. video.mp4 is OBS's file remuxed (no re-encode), one
/// video_frames.csv row per frame.
/// </summary>
public static class ObsRecorder
{
    private static readonly TimeSpan FirstFrameTimeout = TimeSpan.FromSeconds(15), StopTimeout = TimeSpan.FromSeconds(60);

    /// <param name="status">Called with started, progress (every second) and stopped.</param>
    /// <returns>Null on success, else the error.</returns>
    public static async Task<string?> RunAsync(ObsRecordOptions options, Action<RecorderStatus> status, CancellationToken stop)
    {
        Directory.CreateDirectory(options.OutDir);
        await using var obs = await ObsClient.ConnectAsync(options.Address, options.Password);
        if ((await obs.RequestAsync("GetRecordStatus"))["outputActive"]?.GetValue<bool>() == true)
            return "OBS is already recording; stop that recording first";
        var video = await obs.RequestAsync("GetVideoSettings");
        double fps = video["fpsNumerator"]!.GetValue<double>() / video["fpsDenominator"]!.GetValue<double>();
        int screenWidth = video["baseWidth"]!.GetValue<int>(), screenHeight = video["baseHeight"]!.GetValue<int>();

        var startUtc = DateTimeOffset.UtcNow;
        double startQpcMs = QpcClock.NowMs();
        await obs.RequestAsync("StartRecord");
        double t0 = (startQpcMs + QpcClock.NowMs()) / 2;
        string? error = null;
        bool recording = false;
        var firstFrameBy = Stopwatch.StartNew();
        while (!stop.IsCancellationRequested)
        {
            if (recording = (await PollAsync(obs, fps)).Frames > 0) break;
            if (firstFrameBy.Elapsed > FirstFrameTimeout)
            {
                error = "OBS recorded no frame (check its output settings and disk space)";
                break;
            }
            await Task.Delay(20);
        }
        if (!recording) error ??= "stopped before OBS recorded a frame";
        if (error is null)
        {
            status(new RecorderStatus(RecorderStatus.Started, Width: video["outputWidth"]!.GetValue<int>(),
                Height: video["outputHeight"]!.GetValue<int>(), Fps: fps));
            while (!stop.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(1000, stop);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                var (active, frames) = await PollAsync(obs, fps);
                if (!active)
                {
                    error = "OBS stopped recording on its own";
                    break;
                }
                status(new RecorderStatus(RecorderStatus.Progress, frames));
            }
        }
        double stopQpcMs = QpcClock.NowMs();

        string? obsFile = null;
        double? stopRequestQpcMs = null;
        if ((await obs.RequestAsync("GetRecordStatus"))["outputActive"]?.GetValue<bool>() == true)
        {
            stopRequestQpcMs = QpcClock.NowMs();
            var stopped = obs.WaitForRecordStateAsync("OBS_WEBSOCKET_OUTPUT_STOPPED");
            obsFile = (string?)(await obs.RequestAsync("StopRecord"))["outputPath"];
            obsFile = (string?)(await stopped.WaitAsync(StopTimeout))["outputPath"] ?? obsFile;
        }

        string videoPath = Path.Combine(options.OutDir, "video.mp4");
        Mp4Info? info = null;
        if (error is null)
        {
            error = obsFile is null ? "OBS did not say where it saved the recording" : Remux(options.Ffmpeg, obsFile, videoPath);
            if (error is null)
            {
                try
                {
                    info = Mp4Reader.Read(videoPath);
                    // OBS's stop can leave a last sample that counts in the sample table but does not decode; spec v2 §1.5.6
                    // counts frames, so the file is remuxed once more without it.
                    long decoded = VideoTrim.DecodedFrameCount(options.Ffmpeg, videoPath);
                    if (decoded < info.Frames && Remux(options.Ffmpeg, videoPath, videoPath + ".fix.mp4", frames: decoded) is null)
                    {
                        File.Move(videoPath + ".fix.mp4", videoPath, true);
                        info = Mp4Reader.Read(videoPath);
                    }
                    File.Delete(obsFile!);
                }
                catch (Exception e) when (e is InvalidDataException or InvalidOperationException)
                {
                    error = $"cannot read the remuxed OBS recording: {e.Message}";
                }
            }
        }

        long written = info?.Frames ?? 0;
        using (var log = new VideoFrameLogWriter(new StreamWriter(Path.Combine(options.OutDir, "video_frames.csv"))))
            for (long i = 0; i < written; i++)
            {
                double t = t0 + i * 1000.0 / fps;
                log.Write(new GridTick(i, t, t, false));
            }
        var meta = new Dictionary<string, object?>
        {
            ["capture_id"] = Guid.NewGuid().ToString("N"),
            ["tool_version"] = BuildInfo.Version,
            ["width"] = info?.Width ?? video["outputWidth"]!.GetValue<int>(),
            ["height"] = info?.Height ?? video["outputHeight"]!.GetValue<int>(),
            ["screen_width"] = screenWidth,
            ["screen_height"] = screenHeight,
            ["fps"] = fps,
            ["encoder"] = "obs",
            ["source"] = $"obs:{options.Address}",
            ["started_utc"] = startUtc.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
            ["started_qpc_ms"] = startQpcMs,
            ["stopped_qpc_ms"] = stopQpcMs,
            ["frames"] = written,
            ["duplicate_frames"] = null,
            ["dropped_source_frames"] = null,
            ["obs"] = new Dictionary<string, object?>
            {
                ["file"] = obsFile,
                ["first_frame_qpc_ms"] = t0,
                // Cross-check: the file should end at StopRecord (measured: 40–75 ms after t0 + frames / fps).
                ["first_frame_from_stop_qpc_ms"] = stopRequestQpcMs - written * 1000.0 / fps,
            },
            ["error"] = error,
        };
        File.WriteAllText(Path.Combine(options.OutDir, "recording.json"), JsonSerializer.Serialize(meta,
            new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
        status(new RecorderStatus(RecorderStatus.Stopped, written, Width: info?.Width ?? 0, Height: info?.Height ?? 0, Fps: fps,
            Error: error));
        return error;
    }

    /// <returns>Whether OBS is recording, and how many frames its output has counted.</returns>
    private static async Task<(bool Active, long Frames)> PollAsync(ObsClient obs, double fps)
    {
        var s = await obs.RequestAsync("GetRecordStatus");
        double duration = s["outputDuration"]?.GetValue<double>() ?? 0;
        return (s["outputActive"]?.GetValue<bool>() == true, (long)Math.Round(duration * fps / 1000));
    }

    /// <summary>OBS's file (MKV, MP4 or fragmented MP4) into a plain MP4, streams copied.</summary>
    /// <param name="frames">Keep only this many video samples (the decodable ones); null keeps all.</param>
    private static string? Remux(string ffmpeg, string from, string to, long? frames = null)
    {
        var psi = new ProcessStartInfo(ffmpeg) { UseShellExecute = false, RedirectStandardError = true };
        var args = new List<string> { "-y", "-loglevel", "error", "-i", from, "-map", "0:v:0", "-map", "0:a?", "-c", "copy" };
        if (frames is long n) args.AddRange(["-frames:v", n.ToString(System.Globalization.CultureInfo.InvariantCulture)]);
        args.AddRange(["-movflags", "+faststart", to]);
        foreach (string a in args) psi.ArgumentList.Add(a);
        using var proc = Process.Start(psi)!;
        string stderr = proc.StandardError.ReadToEnd();
        proc.WaitForExit();
        return proc.ExitCode == 0 ? null : $"ffmpeg could not remux {from}: {stderr.Trim()}";
    }
}
