namespace Odx.Core;

/// <summary>
/// Where each captured audio packet goes in a continuous PCM stream whose sample 0 is at <see cref="OriginMs"/> on the
/// QPC clock: a packet is placed at its capture time, padded with silence when it comes later than the stream's end and
/// trimmed when it overlaps it, both only beyond <see cref="ToleranceMs"/> so the device clock's jitter against QPC does
/// not pad or trim every packet. Nothing goes past <see cref="EndMs"/> once it is set (the stop): the capture may run on
/// while the video drains, the file does not. Pure bookkeeping: the caller writes the bytes.
/// </summary>
public sealed class AudioTimeline(int sampleRate, double toleranceMs)
{
    public int SampleRate { get; } = sampleRate;
    /// <summary>How far a packet may be from the stream's end before silence is padded or samples trimmed.</summary>
    public double ToleranceMs { get; set; } = toleranceMs;
    public double? OriginMs { get; private set; }
    /// <summary>QPC time (ms) the stream ends at; null while recording.</summary>
    public double? EndMs { get; set; }
    public long FramesWritten { get; private set; }
    public long PaddedFrames { get; private set; }
    /// <summary>Frames of packets not written: overlapping the stream's end, before the origin or past the end.</summary>
    public long TrimmedFrames { get; private set; }

    /// <summary>Sample 0's QPC time; the first call wins.</summary>
    public void SetOrigin(double qpcMs) => OriginMs ??= qpcMs;

    public long FrameAt(double qpcMs) => (long)Math.Round((qpcMs - OriginMs!.Value) * SampleRate / 1000);

    /// <summary>Frames left before <see cref="EndMs"/>; unbounded without an end.</summary>
    private long Room => EndMs is double end && OriginMs is not null ? Math.Max(0, FrameAt(end) - FramesWritten) : long.MaxValue;

    /// <summary>
    /// A packet of <paramref name="frames"/> captured at <paramref name="atMs"/>: how many silent frames to write before it,
    /// how many of its leading frames to skip and how many to write from there (fewer than the rest only at the end).
    /// Needs the origin.
    /// </summary>
    public (long Pad, int Skip, int Take) Place(double atMs, int frames)
    {
        long start = FrameAt(atMs), tolerance = (long)(ToleranceMs * SampleRate / 1000);
        long pad = 0;
        int skip = 0;
        if (start > FramesWritten + tolerance) pad = start - FramesWritten;
        else if (start < FramesWritten - tolerance) skip = (int)Math.Min(FramesWritten - start, frames);
        long room = Room;
        pad = Math.Min(pad, room);
        int take = (int)Math.Min(frames - skip, room - pad);
        PaddedFrames += pad;
        TrimmedFrames += frames - take;
        FramesWritten += pad + take;
        return (pad, skip, take);
    }

    /// <summary>Silent frames to write so the stream reaches <paramref name="atMs"/> (or the end); 0 when it already does.</summary>
    public long SilenceTo(double atMs)
    {
        long frames = Math.Max(0, Math.Min(FrameAt(atMs) - FramesWritten, Room));
        PaddedFrames += frames;
        FramesWritten += frames;
        return frames;
    }
}
