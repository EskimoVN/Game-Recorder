using System.Globalization;
using System.Text.Json.Nodes;
using Odx.Core;

namespace Odx.Capture;

/// <summary>What a rule needs before it can run; rules whose inputs are missing or already invalid are skipped.</summary>
internal enum Needs
{
    Session,
    Timestamps,
    /// <summary>Both timestamps parse and carry a zone.</summary>
    AwareTimestamps,
    /// <summary>AwareTimestamps plus numeric duration_ms, duration_seconds, fps and frame_count.</summary>
    Durations,
    FramesFile,
    /// <summary>Every spec column is present.</summary>
    FramesHeader,
    /// <summary>FramesHeader and at least one row.</summary>
    FrameRows,
    /// <summary>FrameRows and some row has camera data (action-only sessions have none).</summary>
    Camera,
    Video,
}

/// <param name="Check">Returns one message per violation (normally zero or one).</param>
internal sealed record Rule(string Id, string Severity, string Spec, Needs Needs, Func<SessionData, IEnumerable<string>> Check);

/// <summary>Every vendor check of Capture Spec v2 §1.5, in report order. Add a v3 rule by appending it here.</summary>
internal static class Rules
{
    /// <summary>Under a frame at 30 fps (33 ms), over the ±RTT/2 of the samples the origin comes from (±15–25 ms seen).</summary>
    public const double GridOriginToleranceMs = 25;

    public static readonly IReadOnlyList<Rule> All =
    [
        // §1.5.1 session.json
        new("session.schema", Issue.ErrorSeverity, "v2 §1.5.1", Needs.Session, SessionSchema.Errors),
        Err("session.timezone", "v2 §1.5.1", Needs.Timestamps, d =>
            If(!d.TimezoneAware, () => "timestamps must be timezone-aware (Z or ±HH:MM)")),
        Err("session.end_after_start", "v2 §1.5.1", Needs.AwareTimestamps, d =>
            If(d.End <= d.Start, () => "ended_at_utc must be after created_at_utc")),
        Err("session.duration_units", "v2 §1.5.1", Needs.Durations, d =>
            If(Math.Abs(d.DurationMs!.Value / 1000 - d.DurationSeconds!.Value) > d.Tol.DurationUnitsSeconds,
                () => $"duration_ms/1000={Fmt(d.DurationMs!.Value / 1000)} vs duration_seconds={Fmt(d.DurationSeconds!.Value)}")),
        Err("session.duration_timestamps", "v2 §1.5.1", Needs.Durations, d =>
        {
            double wallMs = (d.End - d.Start).TotalMilliseconds;
            return If(Math.Abs(d.DurationMs!.Value - wallMs) > d.Tol.DurationTimestampsMs,
                () => $"duration_ms={Fmt(d.DurationMs!.Value)} vs ended-created={Fmt(wallMs)}");
        }),
        Err("session.frame_count_fps", "v2 §1.5.1", Needs.Durations, d =>
        {
            double expected = d.Fps!.Value * d.DurationSeconds!.Value;
            return If(Math.Abs(d.FrameCount!.Value - expected) > d.Tol.FrameCountFrames,
                () => $"frame_count={d.FrameCount} vs fps*duration={expected.ToString("F1", CultureInfo.InvariantCulture)}");
        }),

        // v3 §4: in a room frame 0 is put on round_start before recording (GridOrigin, ±RTT/2); the full clock fit says
        // where it landed. A warning: the frames are all there, their time against the other players is off by this much.
        new("sync.grid_origin", Issue.WarningSeverity, "v3 §4", Needs.Session, d =>
        {
            double off = d.Session["vendor_grid_origin_offset_ms"] is JsonValue v && v.TryGetValue(out double o) ? o : 0;
            return If(Math.Abs(off) > GridOriginToleranceMs, () => $"frame 0 is {Fmt(off)} ms from round_start (limit ±{Fmt(GridOriginToleranceMs)} ms)");
        }),

        // §1.5.2 frames.csv structure
        Err("frames.headers", "v2 §1.5.2", Needs.FramesFile, d =>
        {
            var missing = Frames.Columns.Where(c => !d.Header.Contains(c)).ToList();
            return If(missing.Count > 0, () => $"missing columns: {string.Join(", ", missing)}");
        }),
        Err("frames.row_count", "v2 §1.5.2", Needs.FramesHeader, d =>
            If(d.FrameCount is not double n || d.Rows.Count != n, () => $"{d.Rows.Count} rows vs frame_count={d.Raw("frame_count")}")),
        Err("frames.frame_id_sequence", "v2 §1.5.2", Needs.FrameRows, d =>
            PerFrame(d, i => d.FrameIds[i] != i, "frame_id not 0..N-1")),
        Err("frames.timestamp_monotonic", "v2 §1.5.2", Needs.FrameRows, d =>
            PerFrame(d, i => i > 0 && d.Timestamps[i] - d.Timestamps[i - 1] <= 0, "timestamp_ms not increasing")),
        Err("frames.timestamp_start", "v2 §1.5.2", Needs.FrameRows, d =>
            If(Math.Abs(d.Timestamps[0]) > d.Interval, () => $"timestamp_ms[0]={Fmt(d.Timestamps[0])} should be ≈ 0")),
        Err("frames.timestamp_end", "v2 §1.5.2", Needs.FrameRows, d =>
            If(d.DurationMs is double ms && Math.Abs(d.Timestamps[^1] - ms) > d.Interval + 1,
                () => $"timestamp_ms[-1]={Fmt(d.Timestamps[^1])} vs duration_ms={Fmt(d.DurationMs!.Value)}")),
        Err("frames.frame_interval", "v2 §1.5.2", Needs.FrameRows, d =>
        {
            double limit = Math.Max(d.Tol.FrameSpacingMinMs, d.Tol.FrameSpacingFraction * d.Interval);
            return PerFrame(d, i => i > 0 && d.Timestamps[i] - d.Timestamps[i - 1] is var dt && dt > 0 &&
                                   Math.Abs(dt - d.Interval) > limit,
                $"spacing deviates from {d.Interval.ToString("F2", CultureInfo.InvariantCulture)} ms");
        }),

        // §1.5.3 C2W and §1.5.4 intrinsics (only for sessions with camera data)
        Err("camera.sparse_nulls", "v2 §1.5.3", Needs.Camera, d =>
            PerFrame(d, i => SessionData.CameraColumns.Any(c => d.Rows[i][c] == Frames.Null), "camera columns null mid-session")),
        Err("camera.model", "v2 §1.5.4", Needs.Camera, d =>
            PerFrame(d, i => d.Rows[i]["camera_model"] is var m && m != Frames.Null &&
                             !Frames.CameraModels.Contains(m, StringComparer.OrdinalIgnoreCase),
                $"camera_model must be one of {string.Join(", ", Frames.CameraModels)}")),
        Err("c2w.bottom_row", "v2 §1.5.3", Needs.Camera, d =>
            PerFrame(d, i => d.C2w[i] is { } m && !(m[3, 0] == 0 && m[3, 1] == 0 && m[3, 2] == 0 && m[3, 3] == 1),
                "bottom row must be exactly (0, 0, 0, 1)")),
        Err("c2w.det", "v2 §1.5.3", Needs.Camera, d =>
            PerFrame(d, i => d.C2w[i] is { } m && Geometry.RotationErrors(m).Det > d.Tol.Rotation, "det(R) must be +1 (right-handed)")),
        Err("c2w.orthonormal", "v2 §1.5.3", Needs.Camera, d =>
            PerFrame(d, i => d.C2w[i] is { } m && Geometry.RotationErrors(m).Ortho > d.Tol.Rotation, "R·Rᵀ must be identity")),
        Err("camera.principal_point", "v2 §1.5.4", Needs.Camera, d =>
            PerFrame(d, i => d.Intrinsics[i] is { } k &&
                            ((d.RecordWidth is > 0 and var w && Math.Abs(k.Cx - w / 2) > d.Tol.PrincipalPointPx) ||
                             (d.RecordHeight is > 0 and var h && Math.Abs(k.Cy - h / 2) > d.Tol.PrincipalPointPx)),
                "principal point must be near the image centre")),
        Err("camera.fx_fy", "v2 §1.5.4", Needs.Camera, d =>
            PerFrame(d, i => d.Intrinsics[i] is { } k && Math.Abs(k.Fx - k.Fy) > d.Tol.FxFyRelative * Math.Abs(k.Fy),
                "fx must equal fy for square pixels")),
        Warn("c2w.roll", "v2 §1.5.3", Needs.Camera, d =>
            PerFrame(d, i => d.C2w[i] is { } m && Math.Abs(m[2, 0]) > d.Tol.LevelRoll, "C2W_M20 not ≈ 0 (camera roll)")),
        Warn("c2w.upright", "v2 §1.5.3", Needs.Camera, d =>
            PerFrame(d, i => d.C2w[i] is { } m && m[2, 1] <= 0, "C2W_M21 <= 0 (camera not upright)")),

        // §1.5.5 control / input data
        Err("input.mouse_delta", "v2 §1.5.5", Needs.FrameRows, d =>
            PerFrame(d, i => Delta(d, i, "input_mouse_dx") is null || Delta(d, i, "input_mouse_dy") is null,
                "input_mouse_dx/dy must be numeric, \"0.0\" when no movement")),
        Err("input.keys_without_actions", "v2 §1.5.5", Needs.FrameRows, d =>
            PerFrame(d, i => d.Rows[i]["input_keys"] != Frames.Null && d.Rows[i]["input_actions"] == Frames.Null,
                "active input_keys need non-null input_actions")),
        Err("input.no_input_null", "v2 §1.5.5", Needs.FrameRows, d =>
            PerFrame(d, i => d.Rows[i]["input_keys"] == Frames.Null && d.Rows[i]["input_mouse_buttons"] == Frames.Null &&
                            Delta(d, i, "input_mouse_dx") == 0 && Delta(d, i, "input_mouse_dy") == 0 &&
                            d.Rows[i]["input_actions"] != Frames.Null,
                "frames with no input must have null input_actions")),
        Err("input.delimiter", "v2 §1.5.5", Needs.FrameRows, d =>
            PerFrame(d, i => new[] { "input_keys", "input_actions", "input_mouse_buttons" }.Any(c =>
                    d.Rows[i][c] is var v && v != Frames.Null && (v.Contains(',') || v.Split('|').Any(t => t.Trim().Length == 0))),
                "multi-value fields must be pipe-delimited with no empty tokens")),

        // §1.5.6 video.mp4, read with Odx.Core.Mp4Reader (missing / unreadable video is reported before these run)
        Err("video.resolution", "v2 §1.5.6", Needs.Video, d =>
            If(d.Video!.Width != d.RecordWidth || d.Video.Height != d.RecordHeight,
                () => $"video {d.Video.Width}x{d.Video.Height} vs session {d.Raw("record_width_px")}x{d.Raw("record_height_px")}")),
        Err("video.frame_count", "v2 §1.5.6", Needs.Video, d =>
            If(d.Video!.Frames != d.Rows.Count, () => $"video has {d.Video.Frames} frames vs {d.Rows.Count} frames.csv rows")),
        Err("video.duration", "v2 §1.5.6", Needs.Video, d =>
            If(d.DurationSeconds is double s && Math.Abs(d.Video!.Duration - s) > d.Tol.VideoDurationSeconds,
                () => $"video duration {d.Video!.Duration.ToString("F2", CultureInfo.InvariantCulture)}s vs duration_seconds={Fmt(d.DurationSeconds!.Value)}")),
    ];

    public static bool Applies(Needs needs, SessionData d) => needs switch
    {
        Needs.Session => true,
        Needs.Timestamps => d.TimestampsParsed,
        Needs.AwareTimestamps => d.TimestampsParsed && d.TimezoneAware,
        Needs.Durations => Applies(Needs.AwareTimestamps, d) && d.DurationMs is not null && d.DurationSeconds is not null &&
                           d.Fps is not null && d.FrameCount is not null,
        Needs.FramesFile => true,
        Needs.FramesHeader => d.HeaderComplete,
        Needs.FrameRows => d.HeaderComplete && d.Rows.Count > 0,
        Needs.Camera => Applies(Needs.FrameRows, d) && d.HasCamera,
        Needs.Video => d.Video is not null,
        _ => throw new ArgumentOutOfRangeException(nameof(needs)),
    };

    private static Rule Err(string id, string spec, Needs needs, Func<SessionData, IEnumerable<string>> check) =>
        new(id, Issue.ErrorSeverity, spec, needs, check);

    private static Rule Warn(string id, string spec, Needs needs, Func<SessionData, IEnumerable<string>> check) =>
        new(id, Issue.WarningSeverity, spec, needs, check);

    private static IEnumerable<string> If(bool violated, Func<string> message) => violated ? [message()] : [];

    /// <summary>One message for all offending frames: "&lt;text&gt;: N frame(s), first at frame K".</summary>
    private static IEnumerable<string> PerFrame(SessionData d, Func<int, bool> bad, string text)
    {
        var frames = Enumerable.Range(0, d.Rows.Count).Where(bad).ToList();
        return If(frames.Count > 0, () => $"{text}: {frames.Count} frame(s), first at frame {frames[0]}");
    }

    private static double? Delta(SessionData d, int row, string column) =>
        d.Rows[row][column] is var s && s != Frames.Null &&
        double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : null;

    private static string Fmt(double v) => Frames.FormatDouble(v);
}
