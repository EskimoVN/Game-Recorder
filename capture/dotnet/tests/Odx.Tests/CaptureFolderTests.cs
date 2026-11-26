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
    public void Tidy_keeps_the_episode_on_top_and_the_session_builds_again_from_raw()
    {
        string dir = NewCapture();
        try
        {
            File.WriteAllBytes(Path.Combine(dir, "video.mp4"), [1, 2, 3]);
            SessionBuilder.Build(dir, GameProfile.Load(WriteProfile(dir)));
            string frames = File.ReadAllText(Path.Combine(dir, "frames.csv"));

            CaptureFolder.Tidy(dir);

            Assert.Equal(["episode.json", "frames.parquet", "sensors.json"],
                Directory.GetFiles(dir).Select(Path.GetFileName).Order(StringComparer.Ordinal));
            Assert.Equal([1, 2, 3], File.ReadAllBytes(Path.Combine(dir, "videos", "pov.mp4")));
            Assert.Equal(Path.Combine(dir, VideoFinalize.Delivered), CaptureFolder.Video(dir));
            foreach (string f in new[] { "frames.csv", "frames_bin.bin", "key_bindings.json", "player.json", "session.json",
                         "video_frames.csv", "profile.json" })
                Assert.True(File.Exists(Path.Combine(dir, "raw", f)), f);
            string revision = (string)JsonNode.Parse(File.ReadAllText(Path.Combine(dir, Episode.FileName)))!["revision_at"]!;
            SessionBuilder.Build(dir, GameProfile.Load(Path.Combine(dir, "raw", "profile.json")));
            Assert.Equal(frames, File.ReadAllText(Path.Combine(dir, "frames.csv")));
            Assert.Equal(revision, (string?)JsonNode.Parse(File.ReadAllText(Path.Combine(dir, Episode.FileName)))!["revision_at"]);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    /// <summary>Data Delivery Format §4–§6: episode.json, frames.parquet, streams/*.parquet, games.json and sensors.json agree.</summary>
    [Fact]
    public async Task A_built_capture_is_an_episode_of_the_delivery_format()
    {
        string dir = NewCapture();
        try
        {
            // A level camera every 100 ms from T0 - 50 to T0 + 2000; the video holds T0 .. T0 + 1983.
            var lines = new List<string> { "t_qpc_ms,pos_x,pos_y,pos_z,fwd_x,fwd_y,fwd_z,right_x,right_y,right_z,up_x,up_y,up_z,fov_deg" };
            for (double t = T0 - 50; t <= T0 + 2000; t += 50)
                if ((t - T0) % 100 == 0 || t < T0) lines.Add($"{Odx.Core.Frames.FormatDouble(t)},1,2,1.7,0,1,0,1,0,0,0,0,1,60");
            File.WriteAllLines(Path.Combine(dir, "camera_samples.csv"), lines);
            File.WriteAllText(Path.Combine(dir, "extractor.json"), """
                {"game": "Game", "convention": "rfu", "fov_axis": "vertical", "camera_model": "PINHOLE"}
                """);

            SessionBuilder.Build(dir, GameProfile.Load(WriteProfile(dir)), player: new PlayerIdentity("p1", "Alice"), sessionId: "r01");
            CaptureFolder.Tidy(dir);

            var episode = JsonNode.Parse(File.ReadAllText(Path.Combine(dir, Episode.FileName)))!.AsObject();
            Assert.Equal("cap-0001", (string?)episode["item_id"]);
            Assert.Equal("cap-0001", (string?)episode["recording_id"]);
            Assert.Equal("r01", (string?)episode["session_id"]);
            Assert.Null(episode["replaces_item_id"]);
            Assert.Equal(2.0, episode["duration_s"]!.GetValue<double>(), 9);
            long startNs = Episode.Ns(UtcAtT0);
            Assert.Equal(startNs, episode["clock"]!["capture_start_ns"]!.GetValue<long>());
            Assert.Equal(startNs + 2_000_000_000, episode["clock"]!["capture_end_ns"]!.GetValue<long>());
            var camera = episode["cameras"]!.AsArray().Single()!;
            Assert.True(camera["is_primary"]!.GetValue<bool>());
            Assert.Equal(Frames, camera["frame_count"]!.GetValue<int>());
            Assert.Equal(startNs, camera["first_frame_ns"]!.GetValue<long>());
            Assert.Null(camera["size_bytes"]);  // no video in this capture
            var artifacts = episode["artifacts"]!.AsArray().Select(a => a!.AsObject()).ToList();
            Assert.Equal([Episode.FramesFile, Episode.ActionsFile, Episode.CameraPoseFile, Episode.SensorsFile],
                artifacts.Select(a => (string)a["path"]!));
            Assert.Equal(Frames, artifacts[0]["row_count"]!.GetValue<int>());
            foreach (var entry in artifacts.Append(episode["domain_metadata"]!.AsObject()))
            {
                byte[] bytes = File.ReadAllBytes(Path.Combine(dir, (string)entry["path"]!));
                Assert.Equal(bytes.Length, entry["size_bytes"]!.GetValue<long>());
                Assert.Equal(Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes)), (string?)entry["sha256"]);
            }

            var frames = (await Parquet.Serialization.ParquetSerializer.DeserializeAsync<FrameRecord>(Path.Combine(dir, Episode.FramesFile))).Data;
            Assert.Equal(Frames, frames.Count);
            Assert.Equal(Enumerable.Range(0, Frames).Select(i => (long)i), frames.Select(f => f.camera_frame_index));
            Assert.Equal(startNs, frames[0].timestamp_ns);
            Assert.Equal(16_667_000, frames[1].timestamp_ns - frames[0].timestamp_ns);
            Assert.All(frames, f => Assert.True(f.image_valid));
            Assert.All(frames, f => Assert.Equal(1.7, f.C2W_M23!.Value, 9));
            Assert.All(frames, f => Assert.Null(f.camera_radial_k1));
            Assert.Equal("W", frames[30].input_keys);
            Assert.Null(frames[0].input_keys);

            var actions = (await Parquet.Serialization.ParquetSerializer.DeserializeAsync<ActionRecord>(Path.Combine(dir, Episode.ActionsFile))).Data;
            Assert.Equal(["key_down", "action_start", "key_up", "action_end"], actions.Select(a => a.kind));
            Assert.Equal(["W", "Forward", "W", "Forward"], actions.Select(a => a.value));
            Assert.Equal(Episode.Ns(UtcAtT0 + 400), actions[0].timestamp_ns);

            var poses = (await Parquet.Serialization.ParquetSerializer.DeserializeAsync<PoseRecord>(Path.Combine(dir, Episode.CameraPoseFile))).Data;
            Assert.Equal(20, poses.Count);
            Assert.Equal(startNs, poses[0].timestamp_ns);
            Assert.All(poses, p => Assert.Equal(1.7, p.C2W_M23, 9));

            var games = JsonNode.Parse(File.ReadAllText(Path.Combine(dir, Episode.MetadataFile)))!;
            Assert.Equal("p1", (string?)games["participants"]![0]!["player_id"]);
            Assert.StartsWith("world RFU right-handed", (string?)games["coordinate_system"]!["axes"]);
            Assert.Equal(Episode.CameraId, (string?)games["participants"]![0]!["camera_id"]);
            Assert.Equal("Forward", (string?)games["input_mappings"]!["bindings"]!["W"]);
            var sensor = JsonNode.Parse(File.ReadAllText(Path.Combine(dir, Episode.SensorsFile)))!["sensors"]![0]!;
            Assert.Equal("pinhole", (string?)sensor["camera_model"]);
            Assert.Equal("camera_to_world", (string?)sensor["extrinsics"]!["direction"]);

            Directory.CreateDirectory(Path.Combine(dir, "annotations"));
            File.WriteAllText(Path.Combine(dir, Episode.EventsFile), "{}\n");
            Episode.Seal(dir);
            var events = JsonNode.Parse(File.ReadAllText(Path.Combine(dir, Episode.FileName)))!["artifacts"]!.AsArray()
                .Single(a => (string?)a!["path"] == Episode.EventsFile)!;
            Assert.Equal(3, events["size_bytes"]!.GetValue<long>());
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [SkippableFact]
    public void Seal_describes_the_video_from_the_file()
    {
        Skip.IfNot(SessionFixtures.HasFfmpeg, "ffmpeg not on PATH");
        string dir = NewCapture();
        try
        {
            SessionFixtures.MakeVideo(Path.Combine(dir, "video.mp4"), nFrames: Frames, width: 320, height: 180, fps: Fps);
            SessionBuilder.Build(dir, GameProfile.Load(WriteProfile(dir)));
            CaptureFolder.Tidy(dir);

            var camera = JsonNode.Parse(File.ReadAllText(Path.Combine(dir, Episode.FileName)))!["cameras"]![0]!;
            var mp4 = Mp4Reader.Read(Path.Combine(dir, VideoFinalize.Delivered));
            Assert.Equal("h264", (string?)camera["codec"]);
            Assert.Equal(new FileInfo(Path.Combine(dir, VideoFinalize.Delivered)).Length, camera["size_bytes"]!.GetValue<long>());
            Assert.Equal((long)Math.Round(mp4.VideoBytes * 8 / mp4.DurationSeconds), camera["bitrate_bps"]!.GetValue<long>());
            Assert.Equal(Math.Round(mp4.DurationSeconds / mp4.KeyFrames, 3), camera["keyframe_interval_s"]!.GetValue<double>());
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Style", "IDE1006", Justification = "named as the Parquet columns")]
    public sealed class FrameRecord
    {
        public long timestamp_ns { get; set; }
        public long camera_frame_index { get; set; }
        public bool image_valid { get; set; }
        public double? C2W_M23 { get; set; }
        public double? camera_radial_k1 { get; set; }
        public string? input_keys { get; set; }
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Style", "IDE1006", Justification = "named as the Parquet columns")]
    public sealed class PoseRecord
    {
        public long timestamp_ns { get; set; }
        public double C2W_M23 { get; set; }
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Style", "IDE1006", Justification = "named as the Parquet columns")]
    public sealed class ActionRecord
    {
        public long timestamp_ns { get; set; }
        public string? kind { get; set; }
        public string? value { get; set; }
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

            var events = File.ReadLines(Path.Combine(dir, Episode.EventsFile)).Select(l => JsonNode.Parse(l)!.AsObject()).ToList();
            Assert.Equal(500.0, events[0]["video_time_ms"]!.GetValue<double>(), 3);
            Assert.Equal(0.5, events[0]["time_s"]!.GetValue<double>(), 6);
            Assert.Equal(["event_id", "type", "server_time_us", "local_receive_utc", "local_receive_time_us",
                "server_time_as_local_us", "video_time_ms", "time_s"], events[0].Select(kv => kv.Key));
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

            var events = File.ReadLines(Path.Combine(dir, Episode.EventsFile)).Select(l => JsonNode.Parse(l)!.AsObject()).ToList();
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
