using Odx.Capture;
using Odx.Core;
using Xunit;

namespace Odx.Tests;

public class Arma3CameraTests
{
    private const double T0 = 5_000_000.0;  // QPC ms of video frame 0
    private const int FrameCount = 600, Fps = 60;

    [Fact]
    public void Fixture_parses_and_the_end_line_is_skipped()
    {
        var rows = Arma3Camera.Read(Path.Combine(AppContext.BaseDirectory, "fixtures", Arma3Camera.RawLog));
        Assert.Equal(3, rows.Count);
        Assert.Equal(5000123.25, rows[0].TQpcMs);
        Assert.Equal("GUNNER", rows[2].View);
        Assert.Equal(new Vec3(4523.125, 6020.75, 12.5), rows[0].Position);
        Assert.Equal(new Vec3(0.866025, -0.5, 0), rows[0].Right);
        Assert.Equal(new Vec3(0, 0, 1), rows[0].Up);
        Assert.Equal(new Vec3(0.5, 0.866025, 0), rows[0].Forward);
        Assert.Equal(0.25, rows[2].ObjectFov);
    }

    [Theory]
    [InlineData(0.75, 0.75, 73.7398)]  // engine default: tan(half) = 0.75
    [InlineData(0.75, 0.25, 28.0725)]  // optics zoom
    [InlineData(1.0, 0.75, 90.0)]      // wider profile FOV
    public void Vertical_fov_combines_profile_fov_and_zoom(double fovTop, double objectFov, double expected)
    {
        var row = new Arma3CameraRow(0, "INTERNAL", default, default, default, default, fovTop, objectFov);
        Assert.Equal(expected, Arma3Camera.VfovDeg(row), 3);
    }

    [Fact]
    public void A_round_log_fills_the_camera_columns()
    {
        string root = Path.Combine(Path.GetTempPath(), $"odx-arma3-{Guid.NewGuid():N}");
        string capture = Path.Combine(root, "capture"), control = Path.Combine(root, "odx", "control.txt");
        try
        {
            WriteCapture(capture);
            Arma3Camera.Begin(control, capture);
            string log = File.ReadAllText(control).Trim();
            Assert.Equal(Path.GetFullPath(Path.Combine(capture, Arma3Camera.RawLog)), log);
            int written = WriteExtensionLog(log);

            Assert.Null(Arma3Camera.Collect(control, capture, TimeSpan.FromSeconds(1)));

            Assert.False(File.Exists(control));
            var samples = CameraSampleReader.Read(Path.Combine(capture, "camera_samples.csv"));
            Assert.Equal(written, samples.Count);
            var report = SessionBuilder.Build(capture, GameProfile.Load(WriteProfile(capture)));
            Assert.Equal(FrameCount, report.CameraFrames);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Orthonormalize_fixes_float32_drift_without_flipping_handedness()
    {
        // A basis like the game's: near-orthonormal but off by ~0.1° (a c2w.orthonormal/c2w.det failure, v2 §1.5.3).
        var right = new Vec3(0.705811, -0.708374, 0.001);
        var up = new Vec3(0.466553, 0.464966, 0.75235);
        var forward = new Vec3(0.532959, 0.531006, -0.658768);

        var (r, u, f) = Arma3Camera.Orthonormalize(right, up, forward);

        Assert.Equal(1.0, r.Norm(), 12);
        Assert.Equal(1.0, u.Norm(), 12);
        Assert.Equal(1.0, f.Norm(), 12);
        Assert.Equal(0.0, r.Dot(u), 12);
        Assert.Equal(0.0, r.Dot(f), 12);
        Assert.Equal(0.0, u.Dot(f), 12);
        // C2W columns are right, up, -forward (RUB): det must be +1, as the builder expects.
        Assert.Equal(1.0, r.Dot(u.Cross(-f)), 12);
    }

    [Fact]
    public void No_log_is_a_warning()
    {
        string root = Path.Combine(Path.GetTempPath(), $"odx-arma3-{Guid.NewGuid():N}");
        string control = Path.Combine(root, "control.txt");
        try
        {
            Arma3Camera.Begin(control, root);
            Assert.NotNull(Arma3Camera.Collect(control, root, TimeSpan.Zero));
            Assert.False(File.Exists(control));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>The extension's log at an uneven game frame rate (15–18 ms), starting before video frame 0, ending with "end".</summary>
    private static int WriteExtensionLog(string path)
    {
        var lines = new List<string>
        {
            "t_qpc_ms,view,pos_x,pos_y,pos_z,right_x,right_y,right_z,up_x,up_y,up_z,fwd_x,fwd_y,fwd_z,fov_top,object_fov",
        };
        for (double t = T0 - 200, k = 0; t < T0 + FrameCount * 1000.0 / Fps + 50; t += 15 + k++ * 7 % 4)
            lines.Add($"{Frames.FormatDouble(t)},INTERNAL,{t * 0.001:F4},6020.7500,12.5000,1.000000,0.000000,0.000000,0.000000,0.000000,1.000000,0.000000,1.000000,0.000000,0.750000,0.750000");
        lines.Add(Arma3Camera.EndLine);
        File.WriteAllLines(path, lines);
        return lines.Count - 2;
    }

    private static void WriteCapture(string dir)
    {
        Directory.CreateDirectory(dir);
        using (var frames = new VideoFrameLogWriter(new StreamWriter(Path.Combine(dir, "video_frames.csv"))))
            for (int i = 0; i < FrameCount; i++)
                frames.Write(new GridTick(i, T0 + i * 1000.0 / Fps, T0 + i * 1000.0 / Fps, false));
        using (var clock = new ClockSampleWriter(new StreamWriter(Path.Combine(dir, "clock_samples.csv"))))
            for (int s = -1; s < 12; s++)
                clock.Write(T0 + s * 1000, 1_791_108_000_000 + s * 1000);
        File.WriteAllText(Path.Combine(dir, "recording.json"), """
            {"capture_id": "cap-0001", "width": 320, "height": 180, "screen_width": 1920, "screen_height": 1080, "fps": 60}
            """);
    }

    private static string WriteProfile(string dir)
    {
        string path = Path.Combine(dir, "profile.json");
        File.WriteAllText(path, """
            {"game_title": "Arma 3", "platform": "PC", "localization": "en-US", "camera_source": "arma3-mod",
             "input_mouse_convention": {"maps_to": "camera_look_velocity", "dx_positive": "right",
               "dx_negative": "left", "dy_positive": "down", "dy_negative": "up"},
             "bindings": {"W": "Forward"}}
            """);
        return path;
    }
}
