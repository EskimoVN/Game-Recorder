using System.Globalization;
using Odx.Capture;
using Odx.Core;
using Xunit;

namespace Odx.Tests;

public class Arma3CameraTests
{
    private const double T0 = 5_000_000.0, UtcAtT0 = 1_791_108_000_000.0;  // QPC ms of video frame 0 and its UTC
    private const int Frames = 600, Fps = 60;

    [Fact]
    public void Fixture_parses()
    {
        var rows = Arma3Camera.Read(Path.Combine(AppContext.BaseDirectory, "fixtures", Arma3Camera.RawLog));
        Assert.Equal(3, rows.Count);
        Assert.Equal(new DateTimeOffset(2026, 11, 10, 3, 34, 27, 123, TimeSpan.Zero).ToUnixTimeMilliseconds(), rows[0].UtcMs);
        Assert.Equal(2, rows[2].View);
        Assert.Equal(new Vec3(0.5, 0.866025, 0), rows[0].Forward);
        Assert.Equal(new Vec3(2003.25, 5651.5, 7.812), rows[0].Position);
        Assert.Equal(12.5, rows[2].VfovDeg);
    }

    [Fact]
    public void Rpt_lines_are_recognised_by_their_marker_and_unquoted()
    {
        Assert.Equal("2026-11-10T3:34:27.123,0,1,0,0,0,0,1,0,1,0,1.000,2.000,3.000,73.740",
            Arma3Camera.RowOf(" 3:34:27 \"ODXCAM,2026-11-10T3:34:27.123,0,1,0,0,0,0,1,0,1,0,1.000,2.000,3.000,73.740\""));
        Assert.Null(Arma3Camera.RowOf(" 3:34:27 Mission read."));
        Assert.Equal(new DateTimeOffset(2026, 11, 10, 3, 34, 27, 123, TimeSpan.Zero).ToUnixTimeMilliseconds(), Arma3Camera.UtcMs("2026-11-10T3:34:27.123"));
        Assert.Throws<FormatException>(() => Arma3Camera.UtcMs("2026-11-10 03:34"));
    }

    [Fact]
    public void The_rounds_rows_of_the_rpt_land_on_qpc_and_fill_the_camera_columns()
    {
        string root = Path.Combine(Path.GetTempPath(), $"odx-arma3-{Guid.NewGuid():N}");
        string capture = Path.Combine(root, "capture"), profile = Path.Combine(root, "profile");
        try
        {
            WriteCapture(capture);
            Directory.CreateDirectory(profile);
            File.WriteAllText(Path.Combine(profile, "arma3_x64_2026-11-09_10-00-00.rpt"), "old\n");
            var inRound = WriteRpt(Path.Combine(profile, "arma3_x64_2026-11-10_10-33-00.rpt"));

            Assert.Null(Arma3Camera.Collect(profile, capture, TimeSpan.FromSeconds(1)));

            var samples = CameraSampleReader.Read(Path.Combine(capture, "camera_samples.csv"));
            Assert.Equal(inRound.Count, samples.Count);
            for (int i = 0; i < inRound.Count; i++) Assert.InRange(samples[i].TQpcMs - inRound[i], -1, 1);

            var report = SessionBuilder.Build(capture, GameProfile.Load(WriteProfile(capture)));
            Assert.Equal(Frames, report.CameraFrames);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Orthonormalize_fixes_float32_drift_without_flipping_handedness()
    {
        // A basis like the addon's: near-orthonormal but off by ~0.1° (a c2w.orthonormal/c2w.det failure, v2 §1.5.3).
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
    public void No_rpt_or_no_rows_is_a_warning()
    {
        string root = Path.Combine(Path.GetTempPath(), $"odx-arma3-{Guid.NewGuid():N}");
        try
        {
            string capture = Path.Combine(root, "capture");
            WriteCapture(capture);
            Assert.Contains("no Arma 3 .rpt", Arma3Camera.Collect(Path.Combine(root, "missing"), capture, TimeSpan.Zero));
            string profile = Path.Combine(root, "profile");
            Directory.CreateDirectory(profile);
            File.WriteAllLines(Path.Combine(profile, "arma3_x64.rpt"),
                [" 1:00:00 \"ODXCAM,2020-01-01T0:0:0.0,0,1,0,0,0,0,1,0,1,0,1.000,2.000,3.000,73.740\""]);  // years before the round
            Assert.Contains("no Arma 3 camera rows", Arma3Camera.Collect(profile, capture, TimeSpan.Zero));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>An .rpt with rows at an uneven frame rate from a minute before the round to a minute after; returns the QPC of the rows inside the round's clock samples ± margin.</summary>
    private static List<double> WriteRpt(string path)
    {
        var lines = new List<string> { "=====================================================================", " 3:33:00 Mission read." };
        var inRound = new List<double>();
        double margin = Arma3Camera.Margin.TotalMilliseconds;
        for (double qpc = T0 - 60_000, k = 0; qpc < T0 + Frames * 1000.0 / Fps + 60_000; qpc += 15 + k++ * 7 % 4)
        {
            double utc = UtcAtT0 + (qpc - T0);
            var t = DateTimeOffset.FromUnixTimeMilliseconds((long)utc);
            string stamp = string.Create(CultureInfo.InvariantCulture, $"{t.Year}-{t.Month}-{t.Day}T{t.Hour}:{t.Minute}:{t.Second}.{t.Millisecond}");
            lines.Add(string.Create(CultureInfo.InvariantCulture,
                $" {t.Hour}:{t.Minute:00}:{t.Second:00} \"ODXCAM,{stamp},0,1.000000,0.000000,0.000000,0.000000,0.000000,1.000000,0.000000,1.000000,0.000000,{qpc * 0.001:F3},5650.000,7.800,73.740\""));
            if (utc >= UtcAtT0 - 1000 - margin && utc <= UtcAtT0 + 11_000 + margin) inRound.Add(qpc);  // the clock samples span −1 s … 11 s
        }
        File.WriteAllLines(path, lines);
        return inRound;
    }

    private static void WriteCapture(string dir)
    {
        Directory.CreateDirectory(dir);
        using (var frames = new VideoFrameLogWriter(new StreamWriter(Path.Combine(dir, "video_frames.csv"))))
            for (int i = 0; i < Frames; i++)
                frames.Write(new GridTick(i, T0 + i * 1000.0 / Fps, T0 + i * 1000.0 / Fps, false));
        using (var clock = new ClockSampleWriter(new StreamWriter(Path.Combine(dir, "clock_samples.csv"))))
            for (int s = -1; s < 12; s++)
                clock.Write(T0 + s * 1000, (long)(UtcAtT0 + s * 1000));
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
