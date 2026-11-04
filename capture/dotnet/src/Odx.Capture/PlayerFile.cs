using System.Text.Json.Nodes;
using Odx.Core;

namespace Odx.Capture;

/// <summary>Who recorded a capture: the player's id and name in the room; null for a capture outside a room.</summary>
public sealed record PlayerIdentity(string PlayerId, string Name);

/// <summary>
/// player.json of a player's round folder, as in the client's multiplayer delivery (sample round
/// 20261017-100551-2907ed-r03): the spec v2 session.json fields written as there (vendor_name as the config spells it, times to the second,
/// whole milliseconds, no vendor_* extras: those stay in raw/session.json), plus the player, their ping and clock offset
/// against the time server, and the first frame's UTC.
/// </summary>
public static class PlayerFile
{
    public const string FileName = "player.json";
    public const string OffsetDefinition = "server_clock - player_clock";

    public static void Write(string captureDir, JsonObject session, double firstFrameUtcMs, PlayerIdentity? player,
        string vendorName)
    {
        var json = (JsonObject)session.DeepClone();
        json.Remove("vendor_tool_version");
        json.Remove("vendor_clock_sync");
        json.Remove("vendor_capture_id");
        json["vendor_name"] = vendorName;
        long durationMs = (long)Math.Round(session["duration_ms"]!.GetValue<double>());
        json["created_at_utc"] = UtcText.IsoSeconds(firstFrameUtcMs);
        json["ended_at_utc"] = UtcText.IsoSeconds(firstFrameUtcMs + durationMs);
        json["duration_ms"] = durationMs;
        json["duration_seconds"] = durationMs / 1000.0;
        if (player is not null) json["player"] = new JsonObject { ["player_id"] = player.PlayerId, ["name"] = player.Name };
        string ntpPath = CaptureFolder.Raw(captureDir, "ntp_samples.csv");
        var samples = File.Exists(ntpPath) ? NtpSampleReader.Read(ntpPath) : [];
        if (samples.Count > 0)
        {
            // The lower-RTT half gives the offset (plan §7.4), as for the session's UTC.
            var best = samples.OrderBy(s => s.RttMs).Take((samples.Count + 1) / 2).ToList();
            double rtt = Median(samples.Select(s => s.RttMs));
            json["network"] = new JsonObject { ["ping_ms"] = Math.Round(rtt, 1) };
            json["clock_sync"] = new JsonObject
            {
                ["offset_ms"] = Math.Round(Median(best.Select(s => s.SystemOffsetMs)), 3),
                ["offset_definition"] = OffsetDefinition,
                ["rtt_ms"] = Math.Round(rtt, 3),
            };
        }
        json["recording"] = new JsonObject { ["video_first_frame_utc"] = UtcText.IsoUs(firstFrameUtcMs) };
        File.WriteAllText(Path.Combine(captureDir, FileName), json.ToJsonString(SessionBuilder.Json));
    }

    /// <summary>clock_sync.offset_ms of a round folder's player.json; null without one.</summary>
    public static double? OffsetMs(string captureDir)
    {
        string path = Path.Combine(captureDir, FileName);
        return File.Exists(path) ? JsonNode.Parse(File.ReadAllText(path))?["clock_sync"]?["offset_ms"]?.GetValue<double>() : null;
    }

    private static double Median(IEnumerable<double> values)
    {
        var sorted = values.Order().ToList();
        return sorted[sorted.Count / 2];
    }
}
