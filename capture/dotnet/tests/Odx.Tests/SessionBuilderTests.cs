using System.Text.Json.Nodes;
using Odx.Capture;
using Odx.Core;
using Xunit;

namespace Odx.Tests;

public class SessionBuilderTests
{
    private static readonly Dictionary<string, string> Bindings = new() { ["W"] = "Forward", ["Up"] = "Forward", ["Left"] = "Shoot" };

    private static InputEvent Ev(double t, InputKind kind, string value = "", double dx = 0) => new(t, kind, value, dx, 0);

    [Fact]
    public void Bound_inputs_get_actions_and_unbound_keys_are_dropped()
    {
        var (events, unbound) = SessionBuilder.MapInputs(
        [
            Ev(10, InputKind.KeyDown, "W"), Ev(11, InputKind.KeyDown, "Ctrl"), Ev(12, InputKind.ButtonDown, "Left"),
            Ev(13, InputKind.ButtonDown, "Right"), Ev(20, InputKind.KeyUp, "W"), Ev(21, InputKind.KeyUp, "Ctrl"),
        ], Bindings, 0);
        Assert.Equal(new[]
        {
            (InputKind.KeyDown, "W"), (InputKind.ActionStart, "Forward"), (InputKind.ButtonDown, "Left"),
            (InputKind.ActionStart, "Shoot"), (InputKind.ButtonDown, "Right"), (InputKind.KeyUp, "W"),
            (InputKind.ActionEnd, "Forward"),
        }, events.Select(e => (e.Kind, e.Value)));
        Assert.Equal(new Dictionary<string, int> { ["Ctrl"] = 1 }, unbound);
    }

    [Fact]
    public void Typematic_repeats_do_not_keep_an_action_alive()
    {
        var (events, _) = SessionBuilder.MapInputs(
            [Ev(10, InputKind.KeyDown, "W"), Ev(40, InputKind.KeyDown, "W"), Ev(70, InputKind.KeyDown, "W"), Ev(90, InputKind.KeyUp, "W")],
            Bindings, 0);
        Assert.Equal([InputKind.KeyDown, InputKind.ActionStart, InputKind.KeyUp, InputKind.ActionEnd], events.Select(e => e.Kind));
    }

    [Fact]
    public void Action_stays_active_while_any_of_its_keys_is_held()
    {
        var (events, _) = SessionBuilder.MapInputs(
            [Ev(10, InputKind.KeyDown, "W"), Ev(20, InputKind.KeyDown, "Up"), Ev(30, InputKind.KeyUp, "W"), Ev(40, InputKind.KeyUp, "Up")],
            Bindings, 0);
        var actions = events.Where(e => e.Kind is InputKind.ActionStart or InputKind.ActionEnd).Select(e => (e.Kind, e.TQpcMs));
        Assert.Equal([(InputKind.ActionStart, 10.0), (InputKind.ActionEnd, 40.0)], actions);
    }

    [Fact]
    public void Mouse_motion_before_the_first_frame_is_dropped()
    {
        var (events, _) = SessionBuilder.MapInputs([Ev(5, InputKind.MouseMove, dx: 9), Ev(15, InputKind.MouseMove, dx: 1)], Bindings, 10);
        Assert.Equal([15.0], events.Select(e => e.TQpcMs));
    }

    [Fact]
    public void Built_capture_passes_validation()
    {
        string dir = Path.Combine(Path.GetTempPath(), $"odx-build-{Guid.NewGuid():N}");
        try
        {
            WriteCapture(dir);

            var report = SessionBuilder.Build(dir, GameProfile.Load(WriteProfile(dir)));

            Assert.Equal(SessionFixtures.NFrames, report.FrameCount);
            Assert.Equal(new Dictionary<string, int> { ["Ctrl"] = 1 }, report.UnboundKeys);
            Assert.Empty(SessionValidator.ValidateSession(dir, video: SessionFixtures.HasFfmpeg));
            var session = JsonNode.Parse(File.ReadAllText(Path.Combine(dir, "session.json")))!;
            Assert.Equal("cap-0001", session["session_id"]!.GetValue<string>());
            Assert.Equal("2026-10-04T10:00:00.000Z", session["created_at_utc"]!.GetValue<string>());
            var (_, rows) = Csv.ReadFile(Path.Combine(dir, "frames.csv"));
            Assert.Equal("W", rows[30]["input_keys"]);
            Assert.Equal("Forward", rows[30]["input_actions"]);
            Assert.DoesNotContain(rows, r => r["input_keys"].Contains("Ctrl"));
            Assert.Equal(0, report.CameraFrames);
            Assert.Equal(Frames.Null, rows[0]["C2W_M00"]);
            var bindings = JsonNode.Parse(File.ReadAllText(Path.Combine(dir, SessionBuilder.KeyBindingsFile)))!;
            Assert.True(JsonNode.DeepEquals(JsonNode.Parse(File.ReadAllText(Path.Combine(dir, "profile.json"))), bindings));

            var (header, records) = ClientFramesBin.Read(Path.Combine(dir, ClientFramesBin.FileName));
            Assert.Equal(("CLIENT-MP", "client.mp.v1.FrameRecord", "cap-0001"), (header.Magic, header.RecordType, header.SessionId));
            Assert.Equal(SessionFixtures.NFrames, records.Count);
            Assert.Equal(["W"], records[30].InputKeys);
            Assert.Equal(["Forward"], records[30].InputActions);
            Assert.Equal(1_791_108_000_000_000 + (long)Math.Round(30 * 1000.0 / SessionFixtures.Fps * 1000), records[30].ServerTimeUs);
            Assert.False(records[30].HasCamera);

            var player = JsonNode.Parse(File.ReadAllText(Path.Combine(dir, PlayerFile.FileName)))!;
            Assert.Equal("cap-0001", (string?)player["session_id"]);
            Assert.Equal("2026-10-04T10:00:00.000000Z", (string?)player["recording"]!["video_first_frame_utc"]);
            Assert.Equal("2026-10-04T10:00:00Z", (string?)player["created_at_utc"]);
            Assert.Equal("Vendor", (string?)player["vendor_name"]);
            Assert.Equal((long)Math.Round(SessionFixtures.NFrames * 1000.0 / SessionFixtures.Fps), (long)player["duration_ms"]!);
            Assert.Null(player["vendor_clock_sync"]);
            Assert.Null(player["player"]);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Ntp_samples_set_the_session_time_instead_of_the_windows_clock()
    {
        string dir = Path.Combine(Path.GetTempPath(), $"odx-build-{Guid.NewGuid():N}");
        try
        {
            WriteCapture(dir);  // Windows clock: frame 0 at 10:00:00.000Z
            using (var ntp = new NtpSampleWriter(new StreamWriter(Path.Combine(dir, "ntp_samples.csv"))))
            {
                // NTP says the Windows clock is 44 s behind; the high-RTT sample disagrees and is ignored.
                ntp.Write(new NtpSample(5_000_000 - 500, 1_791_108_044_000 - 500, 20, 44_000, "ntp.test"));
                ntp.Write(new NtpSample(5_000_000 + 1000, 1_791_108_044_000 + 1000, 25, 44_000, "ntp.test"));
                ntp.Write(new NtpSample(5_000_000 + 1500, 1_791_108_044_000 + 1500 + 400, 900, 44_400, "ntp.test"));
            }

            var report = SessionBuilder.Build(dir, GameProfile.Load(WriteProfile(dir)));

            Assert.Equal(new ClockSync("sntp", "ntp.test", 2, 25, 44_000), report.Clock);
            var session = JsonNode.Parse(File.ReadAllText(Path.Combine(dir, "session.json")))!;
            Assert.Equal("2026-10-04T10:00:44.000Z", session["created_at_utc"]!.GetValue<string>());
            Assert.Equal("sntp", session["vendor_clock_sync"]!["source"]!.GetValue<string>());
            var player = JsonNode.Parse(File.ReadAllText(Path.Combine(dir, PlayerFile.FileName)))!;
            Assert.Equal(44_000, (double)player["clock_sync"]!["offset_ms"]!);
            Assert.Equal(PlayerFile.OffsetDefinition, (string?)player["clock_sync"]!["offset_definition"]);
            Assert.Equal(25, (double)player["network"]!["ping_ms"]!);
            Assert.Empty(SessionValidator.ValidateSession(dir, video: SessionFixtures.HasFfmpeg));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Extractor_camera_samples_fill_the_camera_columns(bool gap)
    {
        string dir = Path.Combine(Path.GetTempPath(), $"odx-build-{Guid.NewGuid():N}");
        try
        {
            WriteCapture(dir);
            var expected = WriteUnrealCameraSamples(dir, skip: gap ? i => i is >= 50 and < 70 : _ => false);

            var report = SessionBuilder.Build(dir, GameProfile.Load(WriteProfile(dir)));

            var errors = SessionValidator.ValidateSession(dir, video: SessionFixtures.HasFfmpeg).Select(i => i.Rule).ToList();
            var (_, rows) = Csv.ReadFile(Path.Combine(dir, "frames.csv"));
            if (gap)
            {
                Assert.InRange(report.CameraFrames, 1, SessionFixtures.NFrames - 1);
                Assert.Equal(["camera.sparse_nulls"], errors);
                return;
            }
            Assert.Equal(SessionFixtures.NFrames, report.CameraFrames);
            Assert.Empty(errors);
            Assert.Equal("pinhole", rows[40]["camera_model"]);
            for (int k = 0; k < 16; k++)
                Assert.Equal(expected[40][k / 4, k % 4], Frames.ParseDouble(rows[40][Frames.C2wColumns[k]]), 1e-9);
            var fov = Geometry.IntrinsicsFromHfov(90, 320, 180);
            Assert.Equal(fov.Fx, Frames.ParseDouble(rows[40]["camera_fx"]), 1e-9);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    private static string WriteProfile(string dir)
    {
        string path = Path.Combine(dir, "profile.json");
        File.WriteAllText(path, """
            {"game_title": "Synthetic", "platform": "PC", "localization": "en-US",
             "input_mouse_convention": {"maps_to": "camera_look_velocity", "dx_positive": "right",
               "dx_negative": "left", "dy_positive": "down", "dy_negative": "up"},
             "bindings": {"W": "Forward", "Left": "Shoot"}}
            """);
        return path;
    }

    /// <summary>
    /// camera_samples.csv + extractor.json as the extractor DLL writes them for an Unreal game: a roll-free canonical pose
    /// per frame, 2 ms before the frame is captured, stored in Unreal axes (X/Y swapped) and centimetres. Returns the
    /// canonical C2W per frame.
    /// </summary>
    private static List<double[,]> WriteUnrealCameraSamples(string dir, Func<int, bool> skip)
    {
        static Vec3 ToUnreal(Vec3 v) => new(v.Y, v.X, v.Z);
        var poses = new List<double[,]>();
        var lines = new List<string> { "t_qpc_ms,pos_x,pos_y,pos_z,fwd_x,fwd_y,fwd_z,right_x,right_y,right_z,up_x,up_y,up_z,fov_deg" };
        foreach (var f in VideoFrameLogReader.Read(Path.Combine(dir, "video_frames.csv")))
        {
            int i = (int)f.Index;
            var c2w = Geometry.C2wFromForward(Geometry.ForwardFromHeadingPitch(i * 0.75, 5), new Vec3(i * 0.1, 0, 1.7));
            poses.Add(c2w);
            if (skip(i)) continue;
            Vec3 Col(int c) => new(c2w[0, c], c2w[1, c], c2w[2, c]);
            var values = new[] { ToUnreal(Col(3)) * 100, ToUnreal(-Col(2)), ToUnreal(Col(0)), ToUnreal(Col(1)) }
                .SelectMany(v => new[] { v.X, v.Y, v.Z });
            lines.Add(string.Join(",", new[] { f.TSourceMs - 2 }.Concat(values).Append(90.0).Select(Frames.FormatDouble)));
        }
        File.WriteAllLines(Path.Combine(dir, "camera_samples.csv"), lines);
        File.WriteAllText(Path.Combine(dir, "extractor.json"), """
            {"game": "Synthetic", "engine": "fake-unreal", "convention": "unreal", "units_to_world": 0.01,
             "fov_axis": "horizontal", "camera_model": "PINHOLE"}
            """);
        return poses;
    }

    /// <summary>Spec v3 §1: every participant shares the round's session_id; the recorder's capture_id stays as a vendor field.</summary>
    [Fact]
    public void A_rounds_capture_takes_the_rounds_session_id()
    {
        string dir = Path.Combine(Path.GetTempPath(), $"odx-build-{Guid.NewGuid():N}");
        try
        {
            WriteCapture(dir);

            var report = SessionBuilder.Build(dir, GameProfile.Load(WriteProfile(dir)), sessionId: "m1-r01");

            Assert.Equal("m1-r01", report.SessionId);
            var session = JsonNode.Parse(File.ReadAllText(Path.Combine(dir, "session.json")))!;
            Assert.Equal("m1-r01", (string?)session["session_id"]);
            Assert.Equal("cap-0001", (string?)session["vendor_capture_id"]);
            var player = JsonNode.Parse(File.ReadAllText(Path.Combine(dir, PlayerFile.FileName)))!.AsObject();
            Assert.Equal("m1-r01", (string?)player["session_id"]);
            Assert.False(player.ContainsKey("vendor_capture_id"));
            using var bin = File.OpenRead(Path.Combine(dir, ClientFramesBin.FileName));
            Assert.Equal("m1-r01", Odx.Capture.ClientMp.FileHeader.Parser.ParseDelimitedFrom(bin).SessionId);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    /// <summary>A capture folder as odx input + odx record would leave it: 120 frames at 60 fps on an irregular-free grid.</summary>
    private static void WriteCapture(string dir)
    {
        Directory.CreateDirectory(dir);
        const double t0 = 5_000_000.0, utcAtT0 = 1_791_108_000_000.0;  // QPC ms of frame 0; 2026-10-04T10:00:00Z
        using (var frames = new VideoFrameLogWriter(new StreamWriter(Path.Combine(dir, "video_frames.csv"))))
            for (int i = 0; i < SessionFixtures.NFrames; i++)
                frames.Write(new GridTick(i, t0 + i * 1000.0 / SessionFixtures.Fps, t0 + i * 1000.0 / SessionFixtures.Fps, false));
        using (var input = new InputLogWriter(new StreamWriter(Path.Combine(dir, "input_events.csv"))))
        {
            input.Write(new InputEvent(t0 - 3000, InputKind.MouseMove, "", 50, 0));  // before capture: dropped
            input.Write(new InputEvent(t0 + 400, InputKind.KeyDown, "W"));
            input.Write(new InputEvent(t0 + 450, InputKind.KeyDown, "W"));          // typematic repeat
            input.Write(new InputEvent(t0 + 900, InputKind.KeyUp, "W"));
            input.Write(new InputEvent(t0 + 1000, InputKind.KeyDown, "Ctrl"));      // unbound
            input.Write(new InputEvent(t0 + 1100, InputKind.KeyUp, "Ctrl"));
            input.Write(new InputEvent(t0 + 1200, InputKind.MouseMove, "", 3, -1));
        }
        using (var clock = new ClockSampleWriter(new StreamWriter(Path.Combine(dir, "clock_samples.csv"))))
            for (int s = -1; s < 3; s++)
                clock.Write(t0 + s * 1000, (long)(utcAtT0 + s * 1000));
        File.WriteAllText(Path.Combine(dir, "recording.json"), """
            {"capture_id": "cap-0001", "width": 320, "height": 180, "screen_width": 1920, "screen_height": 1080, "fps": 60}
            """);
        if (SessionFixtures.HasFfmpeg) SessionFixtures.MakeVideo(Path.Combine(dir, "video.mp4"));
    }
}

public class GameProfileTests
{
    private static readonly string GamesDir =
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "..", "profiles"));

    [Fact]
    public void Every_shipped_profile_loads()
    {
        var files = Directory.GetFiles(GamesDir, "*.json");
        Assert.NotEmpty(files);
        Assert.All(files, f => Assert.NotEmpty(GameProfile.Load(f).Bindings));
    }

    [Fact]
    public void Cs2_profile_names_the_game_window() =>
        Assert.Equal("cs2", GameProfile.Load(Path.Combine(GamesDir, "cs2.json")).WindowProcess);
}
