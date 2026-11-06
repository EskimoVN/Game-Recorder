using System.Text.Json.Nodes;
using Odx.Core;
using Odx.Protocol;

namespace Odx.Capture;

/// <summary>
/// A player's video on the orchestrator's clock (spec v3 §4): SNTP samples against its time service give QPC → server UTC,
/// and server events get the video time and frame at which they happened. Used by the agent for the round folder's
/// events.jsonl and by odx merge for players/&lt;id&gt;/events.jsonl.
/// </summary>
public static class PlayerTimeline
{
    /// <summary>
    /// The samples against the time service on <paramref name="timePort"/>, their lower-RTT half (plan §7.4: the shortest
    /// round trips have the least path asymmetry) and the QPC → server UTC line fitted to it; null without samples.
    /// </summary>
    public static (List<NtpSample> Samples, List<NtpSample> Best, ClockMap Map)? ServerClock(string captureDir, int timePort)
    {
        string ntpPath = CaptureFolder.Raw(captureDir, "ntp_samples.csv");
        var samples = File.Exists(ntpPath)
            ? NtpSampleReader.Read(ntpPath).Where(s => s.Server.EndsWith($":{timePort}", StringComparison.Ordinal)).ToList()
            : [];
        if (samples.Count == 0) return null;
        var best = samples.OrderBy(s => s.RttMs).Take((samples.Count + 1) / 2).ToList();
        return (samples, best, ClockMap.Fit(best.Select(s => (s.TQpcMs, s.UtcUnixMs)).ToList()));
    }

    public static List<double> FrameUtc(string captureDir, ClockMap map) =>
        VideoFrameLogReader.Read(CaptureFolder.Raw(captureDir, "video_frames.csv")).Select(f => map.ToUnixMs(f.TGridMs)).ToList();

    /// <summary>Each event with video_time_ms and frame_index (the frame on screen then; null outside the video).</summary>
    public static IEnumerable<JsonObject> OnVideo(IEnumerable<JsonObject> events, List<double> frameUtc) => events.Select(e =>
    {
        double t = GameEvents.UtcMs(e);
        var copy = (JsonObject)e.DeepClone();
        AddVideoTime(copy, t, frameUtc);
        return copy;
    });

    private static void AddVideoTime(JsonObject copy, double t, List<double> frameUtc)
    {
        copy["video_time_ms"] = Math.Round(t - frameUtc[0], 1);
        int index = frameUtc.BinarySearch(t);
        if (index < 0) index = ~index - 1;
        copy["frame_index"] = t >= frameUtc[0] && t <= frameUtc[^1] ? index : null;
    }

    public static void WriteEvents(string path, IEnumerable<JsonObject> events) =>
        File.WriteAllLines(path, events.Select(e => e.ToJsonString(AgentMessage.Json)));

    /// <summary>
    /// events.jsonl of a player's round folder: the round's server events (raw/server_events.jsonl) on this player's video,
    /// with the fields of the client's multiplayer delivery: when the event's notice reached this machine
    /// (local_receive_utc, local_receive_time_us; absent when it never did) and the server time on this machine's clock
    /// (server_time_as_local_us, from player.json's offset). Fields are in the sample's order, its times to the microsecond,
    /// without the orchestrator's utc_ms or a frame_index. Without clock samples against the time service, or when the
    /// recording did not stop properly (no raw/recording.json, which odx record writes last: a killed odx record leaves a
    /// truncated video_frames.csv), the events are written without video times.
    /// </summary>
    /// <param name="receipts">When each event's notice arrived on this machine's Windows clock (Unix ms), by event_id.</param>
    /// <returns>The highest event_id of the round; 0 without events.</returns>
    public static long WriteRoundEvents(string captureDir, RoundRecord round, IReadOnlyDictionary<long, double>? receipts = null)
    {
        string serverEvents = CaptureFolder.Raw(captureDir, RoundRecord.EventsFileName);
        if (!File.Exists(serverEvents)) return 0;
        var events = File.ReadLines(serverEvents).Where(l => l.Length > 0).Select(l => JsonNode.Parse(l)!.AsObject()).ToList();
        bool recorded = File.Exists(CaptureFolder.Raw(captureDir, "recording.json"));
        var frameUtc = recorded && ServerClock(captureDir, round.TimePort) is { } clock ? FrameUtc(captureDir, clock.Map) : null;
        double? offsetMs = PlayerFile.OffsetMs(captureDir);
        long last = 0;
        WriteEvents(Path.Combine(captureDir, "events.jsonl"), events.Select(e =>
        {
            var copy = new JsonObject();
            foreach (string key in StampFields)
                if (e[key] is { } value) copy[key] = value.DeepClone();
            foreach (var (key, value) in e)
                if (!StampFields.Contains(key) && key != "utc_ms") copy[key] = value?.DeepClone();
            long id = copy["event_id"]?.GetValue<long>() ?? 0;
            last = Math.Max(last, id);
            if (receipts is not null && receipts.TryGetValue(id, out double localMs))
            {
                copy["local_receive_utc"] = UtcText.IsoUs(localMs);
                copy["local_receive_time_us"] = (long)Math.Round(localMs * 1000);
            }
            if (offsetMs is double offset && copy["server_time_us"]?.GetValue<long>() is long serverUs)
                copy["server_time_as_local_us"] = serverUs - (long)Math.Round(offset * 1000);
            if (frameUtc is { Count: > 0 }) copy["video_time_ms"] = Math.Round(GameEvents.UtcMs(e) - frameUtc[0], 3);
            return copy;
        }).ToList());
        return last;
    }

    private static readonly string[] StampFields = ["event_id", "type", "utc", "server_time_us", "round_index", "session_id"];
}
