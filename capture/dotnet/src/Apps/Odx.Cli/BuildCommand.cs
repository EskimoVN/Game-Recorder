// odx build <capture_dir> --profile <game.json> [--vendor <name>] [--camera <dir>]
// Turns an `odx input` + `odx record` capture folder into a spec v2 session (frames.csv + session.json next to video.mp4),
// ready for `odx validate`. A folder whose video.mp4 is missing while video_raw.mp4 (and audio.wav) are there, because
// the recorder was killed or its finalize failed, gets its video.mp4 first (VideoFinalize). --camera: folder with the
// extractor DLL's camera_samples.csv + extractor.json (default: the capture folder; without them the camera columns are
// null). Exit code 1 if the folder cannot be built.

using Odx.Capture;
using Odx.Core;

namespace Odx.Cli;

public static class BuildCommand
{
    public static int Run(string[] args)
    {
        string? dir = null, profilePath = null, vendor = null, cameraDir = null;
        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--profile" when i + 1 < args.Length: profilePath = args[++i]; break;
                case "--vendor" when i + 1 < args.Length: vendor = args[++i]; break;
                case "--camera" when i + 1 < args.Length: cameraDir = args[++i]; break;
                case var a when !a.StartsWith("--", StringComparison.Ordinal) && dir is null: dir = a; break;
                default: dir = null; i = args.Length; break;
            }
        }
        if (dir is null || profilePath is null)
        {
            Console.Error.WriteLine("usage: odx build <capture_dir> --profile <game.json> [--vendor <name>] [--camera <dir>]");
            return 2;
        }

        try
        {
            if (VideoFinalize.RunIfNeeded(dir, ExternalTools.Ffmpeg()) is { } finalize)
            {
                Console.WriteLine(finalize.Ok
                    ? $"finalized video.mp4: {finalize.Frames} frames{(finalize.HasAudio ? " with sound" : "")}, {finalize.ElapsedMs} ms"
                    : $"video.mp4 not finalized: {finalize.Error}");
                if (!finalize.Ok && !File.Exists(Path.Combine(dir, VideoFinalize.Output))) return 1;
            }
            var config = CaptureConfig.Load();
            if (vendor is not null) config = config with { VendorName = vendor };
            var report = SessionBuilder.Build(dir, GameProfile.Load(profilePath), config, cameraDir);
            Console.WriteLine($"built {report.SessionId}: {report.FrameCount} frames, {report.DurationMs / 1000:F1} s, " +
                              $"camera on {report.CameraFrames} frames");
            var c = report.Clock;
            Console.WriteLine(c.Source == "ntp"
                ? $"clock: NTP {c.Server}, RTT {c.RttMs:F1} ms, Windows clock off by {c.SystemOffsetMs:F1} ms"
                : $"clock: {c.Source} (Windows clock, not checked against NTP; capture with odx input --ntp <server>)");
            if (report.UnboundKeys.Count > 0)
                Console.WriteLine("unbound keys left out of input_keys: " +
                                  string.Join(", ", report.UnboundKeys.Select(kv => $"{kv.Key} ×{kv.Value}")));
            return 0;
        }
        catch (Exception e) when (e is IOException or InvalidDataException or FormatException or System.Text.Json.JsonException)
        {
            Console.Error.WriteLine($"cannot build {dir}: {e.Message}");
            return 1;
        }
    }
}
