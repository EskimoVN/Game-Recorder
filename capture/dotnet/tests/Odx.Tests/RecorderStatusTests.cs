using Odx.Core;
using Xunit;

namespace Odx.Tests;

public class RecorderStatusTests
{
    [Fact]
    public void Round_trips_as_one_snake_case_json_line()
    {
        var status = new RecorderStatus(RecorderStatus.Stopped, 100, 82, 1, 1920, 1080, 60, "ffmpeg exited with code 1");
        string json = status.ToJson();
        Assert.DoesNotContain('\n', json);
        Assert.Contains("\"event\":\"stopped\"", json);
        Assert.Equal(status, RecorderStatus.TryParse(json));
    }

    [Fact]
    public void Carries_the_audio_tracks_state()
    {
        var audio = new AudioStatus(AudioStatus.FromDevice, null, "Speakers", "process loopback: not supported", Timing: "qpc",
            Packets: 1200, Discontinuities: 2, PaddedFrames: 480, SilentRatio: 0.0123, PeakDbfs: -6.5, QpcOffsetMs: 12.3);
        var status = new RecorderStatus(RecorderStatus.Started, Width: 1920, Height: 1080, Fps: 60, Audio: audio);
        string json = status.ToJson();
        Assert.Contains("\"audio\":{\"source\":\"device\"", json);
        Assert.Contains("\"fallback_reason\":", json);
        Assert.Equal(status, RecorderStatus.TryParse(json));
        Assert.Null(RecorderStatus.TryParse(new RecorderStatus(RecorderStatus.Progress).ToJson())!.Audio);
    }

    [Fact]
    public void Says_how_long_each_phase_of_stopping_took()
    {
        var status = new RecorderStatus(RecorderStatus.Stopped, 100, Stop: new StopTiming(120, 35, 30_000, FfmpegKilled: true));
        string json = status.ToJson();
        Assert.Contains("\"stop\":{\"drain_ms\":120,\"audio_ms\":35,\"ffmpeg_ms\":30000,\"ffmpeg_killed\":true,\"tail_frames\":0}", json);
        Assert.Equal(status, RecorderStatus.TryParse(json));
        Assert.Null(RecorderStatus.TryParse(new RecorderStatus(RecorderStatus.Progress).ToJson())!.Stop);
        var givenUp = new StopTiming(5, 35, 400, false, "the GPU did not hand over the last frame within 2 s", 1);
        Assert.Equal(givenUp, RecorderStatus.TryParse(new RecorderStatus(RecorderStatus.Stopped, Stop: givenUp).ToJson())!.Stop);
    }

    [Theory]
    [InlineData("recording 1920x1080 @ 60 fps to C:/x (Ctrl+C to stop)")]
    [InlineData("{not json")]
    public void Other_lines_are_ignored(string line) => Assert.Null(RecorderStatus.TryParse(line));
}

public class CaptureProbeTests
{
    [Fact]
    public void Round_trips_and_a_black_or_frameless_source_is_not_ok()
    {
        var probe = new CaptureProbe(1920, 1080, 10, false, 97.3, "borderless", "window:ArmaReforgerSteam");
        Assert.True(probe.Ok);
        Assert.Equal(probe, CaptureProbe.TryParse(probe.ToJson()));
        Assert.Contains("\"window_style\":\"borderless\"", probe.ToJson());
        Assert.False((probe with { Black = true }).Ok);
        Assert.False((probe with { Frames = 0, Black = false }).Ok);
        Assert.Null(CaptureProbe.TryParse("recording 1920x1080"));
    }

    [Fact]
    public void Says_where_the_sound_would_come_from()
    {
        var fallback = new AudioStatus(AudioStatus.FromDevice, null, "Speakers", "recording only the game's audio needs Windows 10 2004 (build 19041) or newer");
        var probe = new CaptureProbe(1920, 1080, 10, false, 97.3, "borderless", "window:ArmaReforgerSteam", fallback);
        Assert.True(probe.Ok);
        Assert.Contains("\"audio\":{\"source\":\"device\"", probe.ToJson());
        Assert.Equal(probe, CaptureProbe.TryParse(probe.ToJson()));
        Assert.Null(CaptureProbe.TryParse((probe with { Audio = null }).ToJson())!.Audio);
    }
}
