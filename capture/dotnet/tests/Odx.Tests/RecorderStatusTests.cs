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

    [Theory]
    [InlineData("recording 1920x1080 @ 60 fps to C:/x (Ctrl+C to stop)")]
    [InlineData("{not json")]
    public void Other_lines_are_ignored(string line) => Assert.Null(RecorderStatus.TryParse(line));
}
