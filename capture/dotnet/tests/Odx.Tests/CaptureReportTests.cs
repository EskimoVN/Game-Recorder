using Odx.Protocol;
using Xunit;

namespace Odx.Tests;

public class CaptureReportTests
{
    private static CaptureReport Report(long frames = 1416, long? round = 1416, long duplicates = 0, long starved = 0, long black = 0,
        bool video = true, string? origin = CaptureReport.Aligned, double? offset = 3.2, string? failure = null, string? audioError = null) =>
        new(frames, round, 30, duplicates, starved, black, video, "process", origin, offset, failure, audioError);

    [Fact]
    public void A_clean_round_is_ok()
    {
        var r = Report();
        Assert.Equal(CaptureReport.Ok, r.Verdict);
        Assert.Empty(r.Problems());
    }

    [Fact]
    public void Baos_round_of_2026_11_07_is_degraded_by_the_encoder()
    {
        var r = Report(frames: 1416, duplicates: 1430 * 1416 / 1475, starved: 435);
        Assert.Equal(CaptureReport.Degraded, r.Verdict);
        Assert.Equal(["encoder behind: 435 frames dropped, 97 % duplicated"], r.Problems());
    }

    [Fact]
    public void Duplicates_alone_are_not_an_encoder_problem()
    {
        Assert.Equal(CaptureReport.Ok, Report(duplicates: 500).Verdict);   // the game ran at ~20 fps
        Assert.Equal(CaptureReport.Ok, Report(duplicates: 1300).Verdict);  // the player stood still on a static screen
        Assert.Equal(CaptureReport.Ok, Report(duplicates: 1300, starved: 100).Verdict);  // a few starved: under 10 %
    }

    [Fact]
    public void Fewer_frames_than_the_round_fail_whatever_else()
    {
        var r = Report(frames: 900);
        Assert.Equal(CaptureReport.Failed, r.Verdict);
        Assert.Equal(["900/1416 frames"], r.Problems());
        Assert.Equal(CaptureReport.Failed, Report(video: false).Verdict);
        Assert.Equal(CaptureReport.Failed, Report(failure: "odx record did not stop within 60 s; killed").Verdict);
    }

    [Fact]
    public void A_late_start_a_free_grid_an_offset_and_an_audio_error_degrade()
    {
        Assert.Equal(["late start: 1.0 s black"], Report(black: 30).Problems());
        Assert.Equal(["not aligned: time service RTT 180 ms, over 100"], Report(origin: "time service RTT 180 ms, over 100", offset: null).Problems());
        Assert.Equal(["frame 0 off round_start by +41 ms"], Report(offset: 41.2).Problems());
        Assert.Equal(["audio: audio file not written out within 5 s"], Report(audioError: "audio file not written out within 5 s").Problems());
        Assert.Equal(CaptureReport.Degraded, Report(black: 30).Verdict);
        Assert.Equal(CaptureReport.Ok, Report(black: 10).Verdict);  // a third of a second: within the countdown's slack
    }

    [Fact]
    public void The_live_problem_waits_ten_seconds_then_names_the_encoder()
    {
        Assert.Null(CaptureReport.LiveProblem(frames: 150, duplicates: 149, starved: 100, leadingBlack: 0, fps: 30));
        Assert.Null(CaptureReport.LiveProblem(300, 280, 0, 0, 30));  // duplicated, nothing starved: a still screen
        Assert.Equal("encoder behind: 40 frames dropped, 3 % duplicated", CaptureReport.LiveProblem(300, 10, 40, 0, 30));
        Assert.Equal("late start: 0.7 s black", CaptureReport.LiveProblem(300, 0, 0, 20, 30));
        Assert.Null(CaptureReport.LiveProblem(300, 10, 5, 0, 30));
    }

    [Fact]
    public void Outside_a_room_nothing_about_the_round_applies()
    {
        var r = Report(round: null, origin: null, offset: null, frames: 100);
        Assert.Equal(CaptureReport.Ok, r.Verdict);
    }
}
