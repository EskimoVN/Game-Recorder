using Odx.Capture;
using Odx.Core;
using Xunit;

namespace Odx.Tests;

public class RoundQuickCheckTests
{
    private static QuickLevel Level(IEnumerable<QuickItem> items, string id) => items.Single(i => i.Id == id).Level;

    [Fact]
    public void A_four_minute_1600x900_video_at_30_fps_passes()
    {
        var items = RoundQuickCheck.Video(new Mp4Info(1600, 900, 7202, 240.07, true), 33).ToList();
        Assert.All(items, i => Assert.Equal(QuickLevel.Ok, i.Level));
    }

    [Fact]
    public void A_wrong_frame_and_a_long_freeze_fail()
    {
        var items = RoundQuickCheck.Video(new Mp4Info(1600, 866, 6900, 230, true), 250).ToList();
        Assert.Equal(QuickLevel.Fail, Level(items, "size"));
        Assert.Equal(QuickLevel.Fail, Level(items, "freeze"));
    }

    [Fact]
    public void A_freeze_between_150_and_200_ms_is_a_warning() =>
        Assert.Equal(QuickLevel.Warn, Level(RoundQuickCheck.Video(new Mp4Info(1920, 1080, 7200, 240, true), 170), "freeze"));

    [Fact]
    public void Clipping_and_a_long_silence_are_warnings()
    {
        const string log = "[silencedetect @ 0] silence_end: 30.5 | silence_duration: 12.3\n" +
                           "[Parsed_volumedetect_1 @ 0] mean_volume: -27.5 dB\n[Parsed_volumedetect_1 @ 0] max_volume: 0.0 dB\n";
        var items = RoundQuickCheck.Audio(log, 0.05).ToList();
        Assert.Equal(QuickLevel.Warn, Level(items, "silence"));
        Assert.Equal(QuickLevel.Ok, Level(items, "volume"));
        Assert.Equal(QuickLevel.Warn, Level(items, "clipping"));
    }

    [Fact]
    public void Quiet_audio_is_a_warning_and_an_all_silent_track_fails()
    {
        Assert.Equal(QuickLevel.Warn, Level(RoundQuickCheck.Audio("mean_volume: -41.2 dB\nmax_volume: -12.0 dB\n", 0.1), "volume"));
        var silent = RoundQuickCheck.Audio("mean_volume: -inf dB\nmax_volume: -inf dB\n", 1.0).ToList();
        Assert.Equal(QuickLevel.Fail, Level(silent, "silence"));
        Assert.DoesNotContain(silent, i => i.Id == "volume");
    }
}
