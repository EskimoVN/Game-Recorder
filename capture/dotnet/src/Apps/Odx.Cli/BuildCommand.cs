// odx build <capture_dir> --profile <game.json> [--vendor <name>] [--camera <dir>]
// Turns an `odx input` + `odx record` capture folder into an episode (Episode: episode.json, videos/pov.mp4, frames.parquet,
// ...) with the spec v2 session (frames.csv, session.json) and the raw logs in raw/, ready for `odx validate`. A folder whose video.mp4 is missing while video_raw.mp4 (and audio.wav) are there, because
// the recorder was killed or its finalize failed, gets its video.mp4 first (VideoFinalize). --camera: folder with the
// extractor DLL's camera_samples.csv + extractor.json (default: the capture folder; without them the camera columns are
// null). Exit code 1 if the folder cannot be built.

using System.Text.Json.Nodes;
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
                if (!finalize.Ok && !File.Exists(CaptureFolder.Video(dir))) return 1;
            }
            var config = CaptureConfig.Load();
            if (vendor is not null) config = config with { VendorName = vendor };
            var (player, sessionId) = EarlierIdentity(dir);
            var report = SessionBuilder.Build(dir, GameProfile.Load(profilePath), config, cameraDir, player, sessionId);
            CaptureFolder.Tidy(dir);
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

    /// <summary>
    /// A folder built before (in a room) keeps its player and the round's session_id: player.json and session.json hold
    /// them, which odx build alone cannot know.
    /// </summary>
    private static (PlayerIdentity? Player, string? SessionId) EarlierIdentity(string dir)
    {
        string playerPath = CaptureFolder.Find(dir, PlayerFile.FileName), sessionPath = CaptureFolder.Find(dir, "session.json");
        var player = File.Exists(playerPath) ? JsonNode.Parse(File.ReadAllText(playerPath))?["player"] : null;
        string? sessionId = File.Exists(sessionPath) ? (string?)JsonNode.Parse(File.ReadAllText(sessionPath))?["session_id"] : null;
        return (player?["player_id"] is JsonValue id && player["name"] is JsonValue name
            ? new PlayerIdentity(id.GetValue<string>(), name.GetValue<string>())
            : null, sessionId);
    }
}
