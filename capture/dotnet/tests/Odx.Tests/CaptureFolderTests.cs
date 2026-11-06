using System.Text.Json.Nodes;
using Odx.Capture;
using Odx.Core;
using Odx.Protocol;
using Xunit;

namespace Odx.Tests;

public class CaptureFolderTests
{
    private const double T0 = 5_000_000.0, UtcAtT0 = 1_791_108_000_000.0;
    private const int Frames = 120, Fps = 60;

    [Fact]
    public void Tidy_keeps_the_delivered_files_on_top_and_the_session_builds_again_from_raw()
    {
        string dir = NewCapture();
        try
        {
            SessionBuilder.Build(dir, GameProfile.Load(WriteProfile(dir)));
            string frames = File.ReadAllText(Path.Combine(dir, "frames.csv"));

            CaptureFolder.Tidy(dir);

            Assert.Equal(["frames.csv", "frames_bin.bin", "key_bindings.json", "player.json", "session.json"],
                Directory.GetFiles(dir).Select(Path.GetFileName).Order(StringComparer.Ordinal));
            Assert.True(File.Exists(Path.Combine(dir, "raw", "video_frames.csv")));
            Assert.True(File.Exists(Path.Combine(dir, "raw", "profile.json")));
            SessionBuilder.Build(dir, GameProfile.Load(Path.Combine(dir, "raw", "profile.json")));
            Assert.Equal(frames, File.ReadAllText(Path.Combine(dir, "frames.csv")));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Round_events_land_on_the_players_video()
    {
        string dir = NewCapture();
        try
        {
            CaptureFolder.Tidy(dir);
            using (var ntp = new NtpSampleWriter(new StreamWriter(Path.Combine(dir, "raw", "ntp_samples.csv"))))
                for (int s = 0; s < 3; s++)
                    ntp.Write(new NtpSample(T0 + s * 1000, UtcAtT0 + 250 + s * 1000, 10, 250, "orchestrator:12300"));
            double start = UtcAtT0 + 250 + 500;  // server time of video time 500 ms = frame 30
            File.WriteAllLines(Path.Combine(dir, "raw", RoundRecord.EventsFileName),
            [
                new JsonObject { ["type"] = GameEvents.RoundStart, ["utc_ms"] = start, ["event_id"] = 7,
                    ["server_time_us"] = (long)(start * 1000) }.ToJsonString(),
                new JsonObject { ["type"] = GameEvents.RoundEnd, ["utc_ms"] = start + 60_000, ["event_id"] = 8 }.ToJsonString(),
            ]);
            File.WriteAllText(Path.Combine(dir, PlayerFile.FileName), """{"clock_sync": {"offset_ms": 250}}""");

            long last = PlayerTimeline.WriteRoundEvents(dir, new RoundRecord("s", "m", "Game", null, 1, start, null, 12300, []),
                new Dictionary<long, double> { [7] = UtcAtT0 + 510.25 });

            var events = File.ReadLines(Path.Combine(dir, "events.jsonl")).Select(l => JsonNode.Parse(l)!.AsObject()).ToList();
            Assert.Equal(500.0, events[0]["video_time_ms"]!.GetValue<double>(), 3);
            Assert.Equal(["event_id", "type", "server_time_us", "local_receive_utc", "local_receive_time_us",
                "server_time_as_local_us", "video_time_ms"], events[0].Select(kv => kv.Key));
            Assert.Null(events[1]["utc_ms"]);
            Assert.Equal(8, last);
            Assert.Equal((long)((UtcAtT0 + 510.25) * 1000), events[0]["local_receive_time_us"]!.GetValue<long>());
            Assert.Equal(UtcText.IsoUs(UtcAtT0 + 510.25), (string?)events[0]["local_receive_utc"]);
            Assert.Equal((long)((start - 250) * 1000), events[0]["server_time_as_local_us"]!.GetValue<long>());
            Assert.Null(events[1]["local_receive_time_us"]);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Round_events_of_a_killed_recording_get_no_video_time()
    {
        string dir = NewCapture();
        try
        {
            CaptureFolder.Tidy(dir);
            File.Delete(Path.Combine(dir, "raw", "recording.json"));
            string frames = Path.Combine(dir, "raw", "video_frames.csv");
            string rows = File.ReadAllText(frames);
            File.WriteAllText(frames, rows[..(rows.TrimEnd('\n').LastIndexOf('\n') + 1)] + "119,");  // the buffered last row was cut
            using (var ntp = new NtpSampleWriter(new StreamWriter(Path.Combine(dir, "raw", "ntp_samples.csv"))))
                ntp.Write(new NtpSample(T0, UtcAtT0 + 250, 10, 250, "orchestrator:12300"));
            double start = UtcAtT0 + 750;
            File.WriteAllLines(Path.Combine(dir, "raw", RoundRecord.EventsFileName),
                [new JsonObject { ["type"] = GameEvents.RoundStart, ["utc_ms"] = start, ["event_id"] = 7 }.ToJsonString()]);

            long last = PlayerTimeline.WriteRoundEvents(dir, new RoundRecord("s", "m", "Game", null, 1, start, null, 12300, []));

            var events = File.ReadLines(Path.Combine(dir, "events.jsonl")).Select(l => JsonNode.Parse(l)!.AsObject()).ToList();
            Assert.Equal(7, last);
            Assert.Equal(["event_id", "type"], events[0].Select(kv => kv.Key));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    private static string NewCapture()
    {
        string dir = Path.Combine(Path.GetTempPath(), $"odx-folder-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        using (var frames = new VideoFrameLogWriter(new StreamWriter(Path.Combine(dir, "video_frames.csv"))))
            for (int i = 0; i < Frames; i++)
                frames.Write(new GridTick(i, T0 + i * 1000.0 / Fps, T0 + i * 1000.0 / Fps, false));
        using (var input = new InputLogWriter(new StreamWriter(Path.Combine(dir, "input_events.csv"))))
        {
            input.Write(new InputEvent(T0 + 400, InputKind.KeyDown, "W"));
            input.Write(new InputEvent(T0 + 900, InputKind.KeyUp, "W"));
        }
        using (var clock = new ClockSampleWriter(new StreamWriter(Path.Combine(dir, "clock_samples.csv"))))
            for (int s = -1; s < 3; s++)
                clock.Write(T0 + s * 1000, (long)(UtcAtT0 + s * 1000));
        File.WriteAllText(Path.Combine(dir, "recording.json"), """
            {"capture_id": "cap-0001", "width": 320, "height": 180, "screen_width": 1920, "screen_height": 1080, "fps": 60}
            """);
        return dir;
    }

    private static string WriteProfile(string dir)
    {
        string path = Path.Combine(dir, "profile.json");
        File.WriteAllText(path, """
            {"game_title": "Game", "platform": "PC", "localization": "en-US",
             "input_mouse_convention": {"maps_to": "camera_look_velocity", "dx_positive": "right",
               "dx_negative": "left", "dy_positive": "down", "dy_negative": "up"},
             "bindings": {"W": "Forward"}}
            """);
        return path;
    }
}

public class PlayerIdentityTests
{
    [Theory]
    [InlineData("p1", true)]
    [InlineData("thanh_01", true)]
    [InlineData("a-b", true)]
    [InlineData("", false)]
    [InlineData("P1", false)]
    [InlineData("desktop bclrdmb", false)]
    [InlineData("-p1", false)]
    [InlineData("abcdefghijabcdefghijabcdefghijabc", false)]
    public void A_player_id_is_a_safe_folder_name(string id, bool valid) => Assert.Equal(valid, PlayerIdentity.IsValidId(id));
}
