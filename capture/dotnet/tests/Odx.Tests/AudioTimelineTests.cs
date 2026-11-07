using Odx.Core;
using Xunit;

namespace Odx.Tests;

public class AudioTimelineTests
{
    private const int Rate = 48000;
    private const double Tol = 20;

    private static AudioTimeline Started()
    {
        var t = new AudioTimeline(Rate, Tol);
        t.SetOrigin(1000);
        return t;
    }

    [Fact]
    public void Packets_within_tolerance_are_appended_untouched()
    {
        var t = Started();
        Assert.Equal((0L, 0, 480), t.Place(1000, 480));          // 10 ms packet at the origin
        Assert.Equal((0L, 0, 480), t.Place(1010 + 15, 480));     // 15 ms late: appended, not padded
        Assert.Equal((0L, 0, 480), t.Place(1020 - 15, 480));     // 15 ms early: appended, not trimmed
        Assert.Equal(1440, t.FramesWritten);
        Assert.Equal(0, t.PaddedFrames + t.TrimmedFrames);
    }

    [Fact]
    public void A_gap_beyond_tolerance_is_padded_with_silence()
    {
        var t = Started();
        t.Place(1000, 480);
        var (pad, skip, take) = t.Place(1110, 480);
        Assert.Equal(480, take);  // 100 ms after the stream's end at 1010
        Assert.Equal(4800, pad);
        Assert.Equal(0, skip);
        Assert.Equal(4800, t.PaddedFrames);
        Assert.Equal(480 + 4800 + 480, t.FramesWritten);
    }

    [Fact]
    public void An_overlap_beyond_tolerance_is_trimmed()
    {
        var t = Started();
        t.Place(1000, 480);
        var (pad, skip, take) = t.Place(980, 4800);
        Assert.Equal(4800 - 1440, take);  // a 100 ms packet starting 30 ms before the stream's end at 1010
        Assert.Equal(0, pad);
        Assert.Equal(1440, skip);
        Assert.Equal(1440, t.TrimmedFrames);
        Assert.Equal(480 + 4800 - 1440, t.FramesWritten);
    }

    [Fact]
    public void A_packet_straddling_the_origin_keeps_only_its_later_part()
    {
        var t = Started();
        var (pad, skip, _) = t.Place(950, 4800);  // a 100 ms packet starting 50 ms before sample 0
        Assert.Equal(0, pad);
        Assert.Equal(2400, skip);
        Assert.Equal(2400, t.FramesWritten);
    }

    [Fact]
    public void The_first_origin_wins_and_silence_fills_up_to_a_time()
    {
        var t = new AudioTimeline(Rate, Tol);
        t.SetOrigin(1000);
        t.SetOrigin(2000);
        Assert.Equal(1000, t.OriginMs);
        Assert.Equal(2400, t.SilenceTo(1050));
        Assert.Equal(0, t.SilenceTo(1040));  // already past it
        Assert.Equal(2400, t.PaddedFrames);
    }

    [Fact]
    public void Slow_drift_is_corrected_once_it_passes_tolerance_not_every_packet()
    {
        var t = Started();
        int corrections = 0;
        for (int i = 0; i < 100; i++)
        {
            // The device clock runs 1 ms per 10 ms packet slow against QPC: packets come 1 ms later each time.
            var (pad, _, _) = t.Place(1000 + i * 11, 480);
            if (pad > 0) corrections++;
        }
        Assert.InRange(corrections, 4, 5);  // 100 ms of drift over 20 ms steps
        Assert.InRange(t.PaddedFrames, 3840, 4800);
    }

    [Fact]
    public void Nothing_is_written_past_the_end()
    {
        var t = Started();
        t.Place(1000, 480);
        t.EndMs = 1025;  // the stop: 1200 frames into the stream
        Assert.Equal((0L, 0, 720), t.Place(1010, 4800));  // a 100 ms packet straddling the end keeps its first 15 ms
        Assert.Equal((0L, 0, 0), t.Place(1110, 480));     // past the end: dropped
        Assert.Equal(0, t.SilenceTo(2000));               // no padding past it either
        Assert.Equal(1200, t.FramesWritten);
        Assert.Equal(4800 - 720 + 480, t.TrimmedFrames);
    }

    [Fact]
    public void A_gap_is_padded_only_up_to_the_end()
    {
        var t = Started();
        t.Place(1000, 480);
        t.EndMs = 1050;
        var (pad, _, take) = t.Place(1100, 480);  // a packet after the end, 90 ms past the stream
        Assert.Equal(1920, pad);                   // 1010 → 1050
        Assert.Equal(0, take);
        Assert.Equal(2400, t.FramesWritten);
    }
}
