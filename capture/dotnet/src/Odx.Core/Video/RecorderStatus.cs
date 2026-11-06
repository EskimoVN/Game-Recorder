using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Odx.Core;

/// <summary>
/// One line of <c>odx record --json-status</c> stdout: <c>started</c> once capture runs, <c>progress</c> every second,
/// <c>stopped</c> once the capture ended (with <c>Error</c> if it failed), then <c>finalizing</c> (Frames muxed so far)
/// while video_raw.mp4 and audio.wav become video.mp4 and <c>finalized</c> at the end (with <c>Error</c> if that failed).
/// Read by the capture host to show live counters and the phase.
/// </summary>
/// <param name="Audio">The audio track's state when --audio was asked: on <c>started</c> where the sound comes from, on
/// <c>stopped</c> its counters too. Null without --audio.</param>
/// <param name="Stop">On <c>stopped</c>: how long each phase of stopping took.</param>
public sealed record RecorderStatus(string Event, long Frames = 0, long Duplicates = 0, long Dropped = 0, int Width = 0,
    int Height = 0, double Fps = 0, string? Error = null, AudioStatus? Audio = null, StopTiming? Stop = null)
{
    public const string Started = "started", Progress = "progress", Stopped = "stopped", Finalizing = "finalizing", Finalized = "finalized";

    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public string ToJson() => JsonSerializer.Serialize(this, Options);

    /// <summary>Null for lines that are not status JSON (e.g. a human-readable message).</summary>
    public static RecorderStatus? TryParse(string line)
    {
        if (!line.StartsWith('{')) return null;
        try
        {
            return JsonSerializer.Deserialize<RecorderStatus>(line, Options);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

/// <summary>
/// How long stopping took, phase by phase (RecorderStatus.Stop, recording.json → stop): a stop that ran into one of the
/// recorder's time limits says which, instead of the capture host killing the recorder after a minute and knowing nothing.
/// </summary>
/// <param name="DrainMs">Writing the queued frames to ffmpeg.</param>
/// <param name="AudioMs">Padding the audio file to the stop and closing it.</param>
/// <param name="FfmpegMs">ffmpeg finishing the file once its input ended.</param>
/// <param name="FfmpegKilled">ffmpeg did not exit within its limit and was killed: the fragmented video keeps what was
/// written up to the last fragment.</param>
public sealed record StopTiming(long DrainMs, long AudioMs, long FfmpegMs, bool FfmpegKilled)
{
    public JsonNode ToNode() => JsonSerializer.SerializeToNode(this, RecorderStatus.Options)!;
}

/// <summary>
/// What odx record's audio file holds and how it went (RecorderStatus.Audio, recording.json → audio, player.json →
/// recording.audio), so a missing, fallen-back or silent track is seen, not found by listening.
/// </summary>
/// <param name="Source">"process" (the game's own sound, WASAPI process loopback), "device" (everything the default
/// playback device plays) or "none" (not recorded: <paramref name="Error"/> says why).</param>
/// <param name="FallbackReason">Why the game's own sound could not be recorded, when "device" stands in for it.</param>
/// <param name="Timing">"qpc": packets placed by their WASAPI capture time; "arrival": by when they arrived, because the
/// capture times were not on the QPC clock (<paramref name="QpcOffsetMs"/> says how far off the first one was).</param>
/// <param name="Discontinuities">Packets WASAPI flagged as following a gap (its buffer overflowed).</param>
/// <param name="PeakDbfs">The loudest sample of the whole track; null when nothing was captured.</param>
/// <param name="SilentRatio">Share of the track that is silence: padding plus packets flagged silent or below −60 dBFS.</param>
public sealed record AudioStatus(string Source, int? ProcessId = null, string? Device = null, string? FallbackReason = null,
    string? Error = null, string? Timing = null, long Packets = 0, long Discontinuities = 0, long PaddedFrames = 0,
    long TrimmedFrames = 0, double? PeakDbfs = null, double? SilentRatio = null, double? QpcOffsetMs = null)
{
    public const string FromProcess = "process", FromDevice = "device", NotRecorded = "none";

    public JsonNode ToNode() => JsonSerializer.SerializeToNode(this, RecorderStatus.Options)!;
}
