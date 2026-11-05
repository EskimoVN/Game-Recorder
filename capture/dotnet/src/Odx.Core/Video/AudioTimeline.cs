namespace Odx.Core;

/// <summary>
/// Where each captured audio packet goes in a continuous PCM stream whose sample 0 is at <see cref="OriginMs"/> on the
/// QPC clock: a packet is placed at its capture time, padded with silence when it comes later than the stream's end and
/// trimmed when it overlaps it, both only beyond <see cref="ToleranceMs"/> so the device clock's jitter against QPC does
/// not pad or trim every packet. Pure bookkeeping: the caller writes the bytes.
/// </summary>
public sealed class AudioTimeline(int sampleRate, double toleranceMs)
{
    public int SampleRate { get; } = sampleRate;
    /// <summary>How far a packet may be from the stream's end before silence is padded or samples trimmed.</summary>
    public double ToleranceMs { get; set; } = toleranceMs;
    public double? OriginMs { get; private set; }
    public long FramesWritten { get; private set; }
    public long PaddedFrames { get; private set; }
    public long TrimmedFrames { get; private set; }

    /// <summary>Sample 0's QPC time; the first call wins.</summary>
    public void SetOrigin(double qpcMs) => OriginMs ??= qpcMs;

    public long FrameAt(double qpcMs) => (long)Math.Round((qpcMs - OriginMs!.Value) * SampleRate / 1000);

    /// <summary>
    /// A packet of <paramref name="frames"/> captured at <paramref name="atMs"/>: how many silent frames to write before it
    /// and how many of its leading frames to skip. Needs the origin.
    /// </summary>
    public (long Pad, int Skip) Place(double atMs, int frames)
    {
        long start = FrameAt(atMs), tolerance = (long)(ToleranceMs * SampleRate / 1000);
        long pad = 0;
        int skip = 0;
        if (start > FramesWritten + tolerance) pad = start - FramesWritten;
        else if (start < FramesWritten - tolerance) skip = (int)Math.Min(FramesWritten - start, frames);
        PaddedFrames += pad;
        TrimmedFrames += skip;
        FramesWritten += pad + frames - skip;
        return (pad, skip);
    }

    /// <summary>Silent frames to write so the stream reaches <paramref name="atMs"/>; 0 when it already does.</summary>
    public long SilenceTo(double atMs)
    {
        long frames = Math.Max(0, FrameAt(atMs) - FramesWritten);
        PaddedFrames += frames;
        FramesWritten += frames;
        return frames;
    }
}
