using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Odx.Core;

namespace Odx.Capture;

/// <param name="UnboundKeys">Keys and mouse buttons pressed but not in the profile's bindings, with press counts. Spec v2 §1.5
/// requires every active key to map to an action, so they are left out of input_keys / input_mouse_buttons (a side button
/// held for a voice chat's push-to-talk fails the client's input check otherwise).</param>
/// <param name="CameraFrames">Frames with camera columns; 0 for an action-only capture (no camera samples).</param>
/// <param name="GridOriginOffsetMs">Frame 0 against round_start on the orchestrator's clock (session.json vendor_grid_origin_offset_ms); null outside a room.</param>
public sealed record BuildReport(string SessionId, int FrameCount, double DurationMs, IReadOnlyDictionary<string, int> UnboundKeys,
    int CameraFrames, ClockSync Clock, double? GridOriginOffsetMs = null);

/// <summary>Where the session's UTC times come from (spec v3 §4: NTP baseline plus measured offset and RTT).</summary>
/// <param name="Source">"sntp" (ntp_samples.csv: SNTP against <paramref name="Server"/>, a real NTP server alone or the
/// orchestrator's time service in a room), "system" (Windows clock, clock_samples.csv) or "recording" (recording.json).</param>
/// <param name="RttMs">Median round trip of the NTP samples used.</param>
/// <param name="SystemOffsetMs">Median of how far the Windows clock was behind the NTP server (negative: ahead).</param>
public sealed record ClockSync(string Source, string? Server, int Samples, double? RttMs, double? SystemOffsetMs);

/// <summary>
/// Turns a capture folder (odx input + odx record logs) into a spec v2 session: writes frames.csv and session.json next
/// to video.mp4. Frame clock: video_frames.csv grid times. Camera columns come from the extractor DLL's
/// camera_samples.csv + extractor.json when present (in the capture folder or <c>cameraDir</c>), else they are null.
/// </summary>
public static class SessionBuilder
{
    /// <summary>A frame gets no camera if the newest sample before it is older than this (game hitch, profile failure).</summary>
    public const double MaxCameraAgeMs = 100;

    public const string KeyBindingsFile = "key_bindings.json";

    internal static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <param name="config">The client's format names (vendor, frames_bin.bin header); null: odx-capture.json next to odx.</param>
    /// <param name="player">Named in player.json; null for a capture outside a room.</param>
    /// <param name="sessionId">The round's session id (spec v3 §1: shared by every participant); null outside a room, where the
    /// recorder's capture_id serves.</param>
    public static BuildReport Build(string captureDir, GameProfile profile, CaptureConfig? config = null, string? cameraDir = null,
        PlayerIdentity? player = null, string? sessionId = null)
    {
        config ??= CaptureConfig.Load();
        var frames = VideoFrameLogReader.Read(CaptureFolder.Raw(captureDir, "video_frames.csv"));
        if (frames.Count == 0) throw new InvalidDataException("video_frames.csv has no frames");
        var recording = JsonNode.Parse(File.ReadAllText(CaptureFolder.Raw(captureDir, "recording.json")))!.AsObject();
        double fps = Required<double>(recording, "fps");
        string inputPath = CaptureFolder.Raw(captureDir, "input_events.csv");
        var events = File.Exists(inputPath) ? InputLogReader.Read(inputPath) : [];

        var times = frames.Select(f => f.TGridMs).ToList();
        var (mapped, unbound) = MapInputs(events, profile.Bindings, times[0] - 1000.0 / fps);
        var inputs = Frames.AggregateInputs(times, mapped);
        int width = Required<int>(recording, "width"), height = Required<int>(recording, "height");
        var cameras = CameraPerFrame(cameraDir ?? captureDir, frames, width, height);
        var rows = frames.Select((f, i) => new FrameRow(i,
            (long)Math.Round(f.TGridMs - times[0]), cameras?[i]?.C2w, cameras?[i] is null ? null : cameras.Model,
            cameras?[i]?.Intrinsics, inputs[i])).ToList();
        Frames.WriteCsv(Path.Combine(captureDir, "frames.csv"), rows);

        // Duration covers the last frame's display interval, so timestamp_ms[-1] + 1 frame ≈ duration_ms.
        double durationMs = frames.Count * 1000.0 / fps;
        var (clockMap, clock) = ClockFor(captureDir, recording);
        double startUnixMs = clockMap.ToUnixMs(times[0]);
        string captureId = Required<string>(recording, "capture_id");
        sessionId ??= captureId;
        double? gridOriginOffset = GridOriginOffset(recording, clockMap);
        var session = new JsonObject
        {
            ["vendor_name"] = config.VendorName.ToLowerInvariant(),
            ["game_title"] = profile.GameTitle,
            ["session_id"] = sessionId,
            ["created_at_utc"] = UtcText.Iso(startUnixMs),
            ["ended_at_utc"] = UtcText.Iso(startUnixMs + durationMs),
            ["duration_ms"] = durationMs,
            ["duration_seconds"] = durationMs / 1000,
            ["fps"] = fps,
            ["frame_count"] = frames.Count,
            ["record_width_px"] = width,
            ["record_height_px"] = height,
            ["screen_width_px"] = Required<int>(recording, "screen_width"),
            ["screen_height_px"] = Required<int>(recording, "screen_height"),
            ["localization"] = profile.Localization,
            ["platform"] = profile.Platform,
            ["input_mouse_convention"] = profile.InputMouseConvention.DeepClone(),
            // Not in the spec (additionalProperties is allowed): which odx / odx-app build produced the session.
            ["vendor_tool_version"] = BuildInfo.Version,
            // Not in the spec: how created_at_utc / ended_at_utc were obtained (spec v3 §4 asks for offset + RTT).
            ["vendor_clock_sync"] = ClockJson(clock),
            // Not in the spec: the recorder's id of this recording, distinct from the round's session_id.
            ["vendor_capture_id"] = captureId,
            // Not in the spec: the audio track's source and health (odx record --audio; null without it).
            ["vendor_audio"] = recording["audio"]?["status"]?.DeepClone(),
            // Not in the spec: frame 0 against the round's start on the orchestrator's clock, by the full clock fit (a room
            // puts the grid there before recording from a few samples: GridOrigin); null outside a room or with a free grid.
            ["vendor_grid_origin_offset_ms"] = gridOriginOffset,
        };
        File.WriteAllText(Path.Combine(captureDir, "session.json"), session.ToJsonString(Json));
        WriteKeyBindings(captureDir, profile);
        ClientFramesBin.Write(Path.Combine(captureDir, ClientFramesBin.FileName), sessionId, rows,
            times.Select(t => t - times[0]).ToList(), times.Select(clockMap.ToUnixMs).ToList(), config);
        PlayerFile.Write(captureDir, session, startUnixMs, player, config.VendorName);
        return new BuildReport(sessionId, frames.Count, durationMs, unbound, cameras?.Count(c => c is not null) ?? 0, clock, gridOriginOffset);
    }

    private static double? GridOriginOffset(JsonObject recording, ClockMap clockMap) =>
        recording["grid_origin_qpc_ms"] is JsonValue o && o.TryGetValue(out double origin) &&
        recording["round_start_utc_ms"] is JsonValue s && s.TryGetValue(out double start)
            ? Math.Round(clockMap.ToUnixMs(origin) - start, 1) : null;

    /// <summary>The SOW §2 key bindings: the whole game profile, so the bindings come with the game's other settings.</summary>
    private static void WriteKeyBindings(string captureDir, GameProfile profile) =>
        File.WriteAllText(Path.Combine(captureDir, KeyBindingsFile), profile.Json.ToJsonString(Json));

    private sealed class FrameCameras(string model, int count) : List<(double[,] C2w, Intrinsics Intrinsics)?>(count)
    {
        public string Model { get; } = model;
    }

    /// <summary>
    /// For each video frame, the newest camera sample taken at or before the frame was captured (t_source): the Present
    /// that produced the image comes just before Windows.Graphics.Capture delivers it. Null when there are no samples.
    /// </summary>
    private static FrameCameras? CameraPerFrame(string dir, List<GridTick> frames, int width, int height)
    {
        string samplesPath = CaptureFolder.Raw(dir, "camera_samples.csv"), infoPath = CaptureFolder.Raw(dir, "extractor.json");
        if (!File.Exists(samplesPath) || !File.Exists(infoPath)) return null;
        var info = ExtractorInfo.Load(infoPath);
        var samples = CameraSampleReader.Read(samplesPath).OrderBy(s => s.TQpcMs).ToList();
        var result = new FrameCameras(info.CameraModel, frames.Count);
        int j = -1;
        foreach (var f in frames)
        {
            while (j + 1 < samples.Count && samples[j + 1].TQpcMs <= f.TSourceMs) j++;
            result.Add(j >= 0 && f.TSourceMs - samples[j].TQpcMs <= MaxCameraAgeMs
                ? info.ToCanonical(samples[j], width, height)
                : null);
        }
        return result;
    }

    /// <summary>
    /// Keeps bound keys and mouse buttons, adds ActionStart/ActionEnd for them (an action stays active while any of its
    /// inputs is held), drops typematic key repeats and mouse motion from before the first frame's interval.
    /// </summary>
    public static (List<InputEvent> Events, SortedDictionary<string, int> UnboundKeys) MapInputs(
        IEnumerable<InputEvent> events, IReadOnlyDictionary<string, string> bindings, double firstFrameStartMs)
    {
        var result = new List<InputEvent>();
        var unbound = new SortedDictionary<string, int>(StringComparer.Ordinal);
        var down = new HashSet<(bool Key, string Value)>();
        var actionHolders = new Dictionary<string, int>();
        foreach (var e in events.OrderBy(e => e.TQpcMs))
        {
            bool isKey = e.Kind is InputKind.KeyDown or InputKind.KeyUp;
            bool press = e.Kind is InputKind.KeyDown or InputKind.ButtonDown;
            switch (e.Kind)
            {
                case InputKind.MouseMove:
                    if (e.TQpcMs > firstFrameStartMs) result.Add(e);
                    continue;
                case InputKind.KeyDown or InputKind.KeyUp or InputKind.ButtonDown or InputKind.ButtonUp:
                    break;
                default:
                    continue;
            }
            bool bound = bindings.TryGetValue(e.Value, out var action);
            if (!bound)
            {
                if (press && down.Add((isKey, e.Value))) unbound[e.Value] = unbound.GetValueOrDefault(e.Value) + 1;
                if (!press) down.Remove((isKey, e.Value));
                continue;
            }
            if (press ? !down.Add((isKey, e.Value)) : !down.Remove((isKey, e.Value))) continue;
            result.Add(e);
            int holders = actionHolders.GetValueOrDefault(action!) + (press ? 1 : -1);
            actionHolders[action!] = holders;
            if (press && holders == 1) result.Add(new InputEvent(e.TQpcMs, InputKind.ActionStart, action!));
            if (!press && holders == 0) result.Add(new InputEvent(e.TQpcMs, InputKind.ActionEnd, action!));
        }
        return (result, unbound);
    }

    /// <summary>Median of utc_unix_ms − t_qpc_ms over clock_samples.csv; recording.json's start pair if there are none.</summary>
    /// <summary>
    /// QPC → UTC: NTP samples if any (the lower-RTT half, per plan §7.4: the shortest round trips have the least path
    /// asymmetry), else the Windows clock samples, else recording.json's start pair.
    /// </summary>
    private static (ClockMap Map, ClockSync Sync) ClockFor(string captureDir, JsonObject recording)
    {
        string ntpPath = CaptureFolder.Raw(captureDir, "ntp_samples.csv");
        var ntp = File.Exists(ntpPath) ? NtpSampleReader.Read(ntpPath) : [];
        if (ntp.Count > 0)
        {
            var best = ntp.OrderBy(s => s.RttMs).Take((ntp.Count + 1) / 2).OrderBy(s => s.TQpcMs).ToList();
            var sync = new ClockSync("sntp", string.Join(",", best.Select(s => s.Server).Distinct()), best.Count,
                Median(best.Select(s => s.RttMs)), Median(ntp.Select(s => s.SystemOffsetMs)));
            return (ClockMap.Fit(best.Select(s => (s.TQpcMs, s.UtcUnixMs)).ToList()), sync);
        }
        string clockPath = CaptureFolder.Raw(captureDir, "clock_samples.csv");
        var system = File.Exists(clockPath)
            ? Csv.ReadFile(clockPath).Rows.Select(r => (Frames.ParseDouble(r["t_qpc_ms"]), Frames.ParseDouble(r["utc_unix_ms"]))).ToList()
            : [];
        if (system.Count > 0) return (ClockMap.Fit(system), new ClockSync("system", null, system.Count, null, null));
        var started = DateTimeOffset.Parse(Required<string>(recording, "started_utc"), CultureInfo.InvariantCulture);
        return (ClockMap.Fit([(Required<double>(recording, "started_qpc_ms"), started.ToUnixTimeMilliseconds())]),
            new ClockSync("recording", null, 1, null, null));
    }

    private static double Median(IEnumerable<double> values)
    {
        var sorted = values.Order().ToList();
        return sorted[sorted.Count / 2];
    }

    private static JsonObject ClockJson(ClockSync c)
    {
        var json = new JsonObject { ["source"] = c.Source, ["samples"] = c.Samples };
        if (c.Server is not null) json["server"] = c.Server;
        if (c.RttMs is double rtt) json["rtt_ms"] = Math.Round(rtt, 1);
        if (c.SystemOffsetMs is double offset) json["system_clock_offset_ms"] = Math.Round(offset, 1);
        return json;
    }

    private static T Required<T>(JsonObject json, string key) =>
        json[key] is JsonValue v && v.TryGetValue(out T? value) && value is not null
            ? value
            : throw new InvalidDataException($"recording.json: missing or invalid \"{key}\"");
}
