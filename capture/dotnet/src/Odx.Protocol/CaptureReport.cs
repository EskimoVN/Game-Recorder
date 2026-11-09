using System.Text.Json.Serialization;

namespace Odx.Protocol;

/// <summary>
/// How a player's recording of a round went, in numbers, and the verdict the host reads to decide whether to record the
/// round again (document/video-alignment-plan.md §3.4): <see cref="Ok"/>, <see cref="Degraded"/> (the frames are all
/// there, but not good data) or <see cref="Failed"/> (no usable video from this player). Sent in <see cref="Done"/>, kept
/// in round.json for every player, shown on every machine. The same thresholds, on the recorder's live counters, give
/// <see cref="LiveProblem"/> while the round runs.
/// </summary>
/// <param name="Frames">Frames in video.mp4 (what the recorder wrote, when there is no video).</param>
/// <param name="RoundFrames">Frames the round's video has on every machine; null outside a room or with a free grid.</param>
/// <param name="Duplicates">Frames shown again because no new frame reached the encoder in time.</param>
/// <param name="Starved">Captured frames dropped because the encoder had not returned a buffer.</param>
/// <param name="LeadingBlack">Black frames before the first captured one (the recorder started after round_start).</param>
/// <param name="Video">video.mp4 exists.</param>
/// <param name="Audio">The sound's source ("process", "device", "none"); null when not recorded.</param>
/// <param name="GridOrigin"><see cref="Aligned"/>, or why frame 0 is not on round_start; null outside a room.</param>
/// <param name="GridOriginOffsetMs">Frame 0 against round_start by the full clock fit, when the grid was aligned.</param>
/// <param name="Failure">Why the capture failed (the capture host's one line); null when it did not.</param>
/// <param name="AudioError">Why the sound stopped or did not end cleanly; null when it was fine.</param>
/// <param name="StopNote">The stop gave up the last captured frame (the capture callback or the GPU did not answer in
/// time), and why; null when it got it. A failure: the round is recorded again (decided 2026-11-09).</param>
/// <param name="TailFrames">With <paramref name="StopNote"/>: frames at the end that show the frame before instead.</param>
public sealed record CaptureReport(long Frames, long? RoundFrames, double Fps, long Duplicates, long Starved, long LeadingBlack,
    bool Video, string? Audio, string? GridOrigin, double? GridOriginOffsetMs, string? Failure, string? AudioError,
    string? StopNote = null, long TailFrames = 0)
{
    public const string Ok = "ok", Degraded = "degraded", Failed = "failed";
    public const string Aligned = "aligned";
    /// <summary>
    /// Starved frames (captured, dropped because the encoder had not returned a buffer) over this share of the frames say
    /// the encoder is behind. Duplicates alone do not: a player standing still, a loading screen or a game under the grid
    /// rate duplicate frames with a healthy encoder.
    /// </summary>
    public const double MaxStarvedRatio = 0.1;
    /// <summary>Under a frame at 30 fps, over the ±RTT/2 of the samples the origin comes from.</summary>
    public const double MaxGridOriginOffsetMs = 25;
    /// <summary>Encoder checks need this much recorded first: the first seconds always duplicate (start-up, loading screens).</summary>
    public const double SettleSeconds = 10;
    [JsonIgnore]
    public string Verdict =>
        !Video || Failure is not null || StopNote is not null || (RoundFrames is long n && Frames < n) ? Failed
            : Problems().Count > 0 ? Degraded : Ok;

    /// <summary>One line per problem, the one that decides the verdict first; empty when <see cref="Ok"/>.</summary>
    public List<string> Problems()
    {
        var problems = new List<string>();
        if (Failure is not null) problems.Add(Failure);
        else if (!Video) problems.Add("no video");
        if (RoundFrames is long n && Frames < n) problems.Add($"{Frames}/{n} frames");
        if (EncoderBehind(Frames, Duplicates, Starved, Fps) is { } encoder) problems.Add(encoder);
        if (LateStart(LeadingBlack, Fps) is { } late) problems.Add(late);
        if (GridOrigin is { } origin && origin != Aligned) problems.Add($"not aligned: {origin}");
        else if (GridOriginOffsetMs is double off && Math.Abs(off) > MaxGridOriginOffsetMs)
            problems.Add($"frame 0 off round_start by {off:+0;-0} ms");
        if (AudioError is not null) problems.Add($"audio: {AudioError}");
        if (StopNote is not null) problems.Add($"stop: {StopNote}; the last {TailFrames} frame(s) show the frame before");
        return problems;
    }

    /// <summary>The recorder's live counters say the encoder is behind or the start was late; null while all is well (or too early to tell).</summary>
    public static string? LiveProblem(long frames, long duplicates, long starved, long leadingBlack, double fps) =>
        EncoderBehind(frames, duplicates, starved, fps) ?? LateStart(leadingBlack, fps);

    private static string? EncoderBehind(long frames, long duplicates, long starved, double fps)
    {
        if (frames < SettleSeconds * fps || starved <= MaxStarvedRatio * frames) return null;
        return $"encoder behind: {starved} frames dropped, {100.0 * duplicates / frames:0} % duplicated";
    }

    private static string? LateStart(long leadingBlack, double fps) =>
        fps > 0 && leadingBlack > fps / 2 ? $"late start: {leadingBlack / fps:0.0} s black" : null;
}
