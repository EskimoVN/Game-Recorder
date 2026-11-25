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
        Assert.Equal(QuickLevel.Warn, Level(items, "clipping"));
    }

    [Fact]
    public void An_all_silent_track_fails()
    {
        var silent = RoundQuickCheck.Audio("mean_volume: -inf dB\nmax_volume: -inf dB\n", 1.0).ToList();
        Assert.Equal(QuickLevel.Fail, Level(silent, "silence"));
    }

    [Fact]
    public void The_mean_volume_is_no_longer_reported()
    {
        var items = RoundQuickCheck.Audio("mean_volume: -41.2 dB\nmax_volume: -12.0 dB\n", 0.1).ToList();
        Assert.DoesNotContain(items, i => i.Id == "volume");
        Assert.All(items, i => Assert.Equal(QuickLevel.Ok, i.Level));
    }

    private static Dictionary<string, string> Row(string keys, string dx, string dy) => new()
    {
        ["input_keys"] = keys, ["input_actions"] = keys == "null" ? "null" : "forward",
        ["input_mouse_buttons"] = "null", ["input_mouse_dx"] = dx, ["input_mouse_dy"] = dy,
    };

    [Fact]
    public void Frames_with_keys_and_mouse_movement_pass_and_are_counted()
    {
        var rows = new[] { Row("w", "0.0", "0.0"), Row("w|shift", "1.5", "0.0"), Row("null", "0.0", "-2.0"), Row("null", "0.0", "0.0") };
        var items = RoundQuickCheck.Input(rows).ToList();
        Assert.Equal(new QuickItem("input_keys", QuickLevel.Ok, "2"), items.Single(i => i.Id == "input_keys"));
        Assert.Equal(new QuickItem("input_mouse", QuickLevel.Ok, "2"), items.Single(i => i.Id == "input_mouse"));
    }

    [Fact]
    public void A_round_without_any_key_fails()
    {
        var rows = new[] { Row("null", "3.0", "1.0"), Row("null", "0.0", "0.0") };
        var items = RoundQuickCheck.Input(rows).ToList();
        Assert.Equal(new QuickItem("input_keys", QuickLevel.Fail, "0"), items.Single(i => i.Id == "input_keys"));
        Assert.Equal(QuickLevel.Ok, Level(items, "input_mouse"));
    }

    [Fact]
    public void A_round_without_any_mouse_movement_fails()
    {
        var rows = new[] { Row("w", "0.0", "0.0"), Row("null", "null", "null") };
        var items = RoundQuickCheck.Input(rows).ToList();
        Assert.Equal(QuickLevel.Ok, Level(items, "input_keys"));
        Assert.Equal(new QuickItem("input_mouse", QuickLevel.Fail, "0"), items.Single(i => i.Id == "input_mouse"));
    }

    [Fact]
    public void An_empty_frames_csv_fails_both_input_items() =>
        Assert.All(RoundQuickCheck.Input(Array.Empty<Dictionary<string, string>>()), i => Assert.Equal(QuickLevel.Fail, i.Level));

    [Fact]
    public void Input_is_read_from_the_round_frames_csv()
    {
        string dir = Directory.CreateTempSubdirectory("odx-quick-").FullName;
        try
        {
            File.WriteAllText(Path.Combine(dir, "frames.csv"),
                "frame_id,timestamp_ms,input_keys,input_actions,input_mouse_buttons,input_mouse_dx,input_mouse_dy\n" +
                "0,0,w,forward,null,0.0,0.0\n1,33,null,null,null,4.0,0.0\n");
            var items = RoundQuickCheck.Input(dir).ToList();
            Assert.Equal("1", items.Single(i => i.Id == "input_keys").Value);
            Assert.Equal("1", items.Single(i => i.Id == "input_mouse").Value);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void A_missing_frames_csv_fails_input()
    {
        string dir = Directory.CreateTempSubdirectory("odx-quick-").FullName;
        try
        {
            var items = RoundQuickCheck.Input(dir).ToList();
            Assert.Equal(2, items.Count);
            Assert.All(items, i => Assert.Equal(new QuickItem(i.Id, QuickLevel.Fail, "0"), i));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void Every_delivered_file_present_and_not_empty_is_ok()
    {
        string dir = Directory.CreateTempSubdirectory("odx-quick-").FullName;
        try
        {
            foreach (string name in RoundQuickCheck.RequiredFiles) File.WriteAllText(Path.Combine(dir, name), "x");
            Assert.Equal(new QuickItem("files", QuickLevel.Ok, "ok"), RoundQuickCheck.Files(dir));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void Missing_and_empty_delivered_files_fail_and_are_named()
    {
        string dir = Directory.CreateTempSubdirectory("odx-quick-").FullName;
        try
        {
            foreach (string name in RoundQuickCheck.RequiredFiles) File.WriteAllText(Path.Combine(dir, name), "x");
            File.Delete(Path.Combine(dir, "key_bindings.json"));
            File.WriteAllText(Path.Combine(dir, "frames.csv"), "");
            Assert.Equal(new QuickItem("files", QuickLevel.Fail, "frames.csv, key_bindings.json"), RoundQuickCheck.Files(dir));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void A_failed_capture_reports_its_video_only()
    {
        string dir = Directory.CreateTempSubdirectory("odx-quick-").FullName;
        try
        {
            var failed = new RunResult(dir, false, null, 0, [], null, "recorder exited");
            Assert.Equal(new[] { new QuickItem("size", QuickLevel.Fail, "no video.mp4") }, RoundQuickCheck.Run(failed, "ffmpeg"));
            var kept = RoundQuickCheck.Run(failed with { CaptureOk = true }, "ffmpeg");
            Assert.Equal(new[] { "size", "input_keys", "input_mouse", "files" }, kept.Select(i => i.Id));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void Required_files_are_the_delivered_ones_but_the_room_events()
    {
        Assert.Equal(CaptureFolder.Delivered.Where(n => n != "events.jsonl"), RoundQuickCheck.RequiredFiles);
        Assert.Contains("video.mp4", RoundQuickCheck.RequiredFiles);
        Assert.Contains("frames.csv", RoundQuickCheck.RequiredFiles);
        Assert.Contains("session.json", RoundQuickCheck.RequiredFiles);
        Assert.Contains("key_bindings.json", RoundQuickCheck.RequiredFiles);
    }
}
