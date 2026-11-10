using System.Globalization;
using Odx.Core;

namespace Odx.Capture;

/// <summary>One rendered frame of the odx_capture addon's log, in Arma's world axes (X east, Y north, Z up, ASL metres).</summary>
public sealed record Arma3CameraRow(double UtcMs, int View, Vec3 Right, Vec3 Up, Vec3 Forward, Vec3 Position, double VfovDeg);

/// <summary>
/// Arma 3's camera (document/games/arma3.md §3, §5). The odx_capture client addon logs the render camera every rendered
/// frame to the game's .rpt through diag_log, stamped with the Windows clock to the millisecond (systemTimeUTC), in the
/// profile folder OdxArma3.exe starts the game with. After a round, the rows within the round's clock samples are copied
/// into the capture folder as the raw log (pinned by dotnet/fixtures/arma3_camera.csv), and clock_samples.csv puts them
/// on QPC: camera_samples.csv + extractor.json, which <see cref="SessionBuilder"/> reads like the extractor DLL's.
/// </summary>
public static class Arma3Camera
{
    /// <summary>The game profile's camera_source for this route.</summary>
    public const string Source = "arma3-mod";
    public const string RawLog = "arma3_camera.csv";
    /// <summary>The addon's line marker in the .rpt; diag_log writes the string in quotes after the time of day.</summary>
    public const string Marker = "ODXCAM,";
    /// <summary>Rows this far outside the capture's clock samples are kept too: the samples start after the first frame.</summary>
    public static readonly TimeSpan Margin = TimeSpan.FromSeconds(5);

    private static readonly string[] Header =
    [
        "utc", "view", "right_x", "right_y", "right_z", "up_x", "up_y", "up_z", "fwd_x", "fwd_y", "fwd_z",
        "pos_x", "pos_y", "pos_z", "vfov_deg",
    ];

    /// <summary>
    /// The profile folder OdxArma3.exe starts the game with (-profiles, where the .rpt goes): its saved "profile" setting,
    /// else its default Documents\OdxArma3\profile.
    /// </summary>
    public static string ProfileDir()
    {
        string settings = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "OdxArma3", "settings.txt");
        string? profile = File.Exists(settings)
            ? File.ReadLines(settings).Where(l => l.StartsWith("profile=", StringComparison.Ordinal)).Select(l => l[8..]).LastOrDefault()
            : null;
        return string.IsNullOrWhiteSpace(profile)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "OdxArma3", "profile")
            : profile;
    }

    /// <summary>
    /// Copies the round's rows out of the newest .rpt in <paramref name="profileDir"/> (waiting up to <paramref name="wait"/>
    /// for the game to flush the last ones) and converts them. Returns a warning when there is no camera for this round;
    /// the capture itself is kept either way.
    /// </summary>
    public static string? Collect(string profileDir, string captureDir, TimeSpan wait)
    {
        string? rpt = Directory.Exists(profileDir)
            ? Directory.GetFiles(profileDir, "*.rpt").OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault()
            : null;
        if (rpt is null) return $"no Arma 3 .rpt in {profileDir} (game not started with OdxArma3.exe?)";
        string clockPath = CaptureFolder.Raw(captureDir, "clock_samples.csv");
        if (!File.Exists(clockPath)) return "no clock_samples.csv to place the Arma 3 camera on QPC";
        var utc = Csv.ReadFile(clockPath).Rows.Select(r => Frames.ParseDouble(r["utc_unix_ms"])).ToList();
        if (utc.Count == 0) return "clock_samples.csv is empty; the Arma 3 camera cannot be placed on QPC";
        double from = utc.Min() - Margin.TotalMilliseconds, to = utc.Max() + Margin.TotalMilliseconds;

        List<string> rows;
        var deadline = DateTime.UtcNow + wait;
        while (true)
        {
            rows = RowsBetween(rpt, from, to, out double last);
            if (last >= utc.Max() || DateTime.UtcNow >= deadline) break;
            Thread.Sleep(200);
        }
        if (rows.Count == 0) return $"no Arma 3 camera rows for this round in {Path.GetFileName(rpt)} (addon not loaded?)";
        try
        {
            File.WriteAllLines(CaptureFolder.Raw(captureDir, RawLog), rows.Prepend(string.Join(",", Header)));
            return Convert(captureDir);
        }
        catch (Exception e) when (e is IOException or InvalidDataException or FormatException)
        {
            return $"Arma 3 camera log not converted: {e.Message}";
        }
    }

    /// <summary>The addon's rows of an .rpt (the game keeps it open for writing) with a UTC in [from, to]; <paramref name="lastUtcMs"/> is the newest row's time, NaN without rows.</summary>
    public static List<string> RowsBetween(string rpt, double from, double to, out double lastUtcMs)
    {
        var rows = new List<string>();
        lastUtcMs = double.NaN;
        using var stream = new FileStream(rpt, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        while (reader.ReadLine() is { } line)
        {
            if (RowOf(line) is not { } row) continue;
            double t = UtcMs(row.AsSpan(0, row.IndexOf(',')));
            lastUtcMs = t;
            if (t >= from && t <= to) rows.Add(row);
        }
        return rows;
    }

    /// <summary>The csv row inside an .rpt line (<c>HH:MM:SS "ODXCAM,…"</c>), or null for any other line.</summary>
    public static string? RowOf(string line)
    {
        int at = line.IndexOf(Marker, StringComparison.Ordinal);
        if (at < 0) return null;
        string row = line[(at + Marker.Length)..];
        int quote = row.IndexOf('"');
        return quote < 0 ? row : row[..quote];
    }

    /// <summary>"2026-11-10T3:34:27.123" (systemTimeUTC's parts, unpadded) → Unix ms.</summary>
    public static double UtcMs(ReadOnlySpan<char> text)
    {
        var p = new int[7];
        int n = 0, value = 0;
        foreach (char c in text)
        {
            if (c is '-' or 'T' or ':' or '.')
            {
                if (n == 6) throw new FormatException($"bad time \"{text}\"");
                p[n++] = value;
                value = 0;
            }
            else if (c is >= '0' and <= '9') value = value * 10 + (c - '0');
            else throw new FormatException($"bad time \"{text}\"");
        }
        if (n != 6) throw new FormatException($"bad time \"{text}\"");
        p[6] = value;
        return new DateTimeOffset(p[0], p[1], p[2], p[3], p[4], p[5], p[6], TimeSpan.Zero).ToUnixTimeMilliseconds();
    }

    /// <summary>arma3_camera.csv + clock_samples.csv → camera_samples.csv + extractor.json; a warning or null.</summary>
    public static string? Convert(string captureDir)
    {
        var rows = Read(CaptureFolder.Raw(captureDir, RawLog));
        if (rows.Count == 0) return "the Arma 3 camera log is empty";
        string clockPath = CaptureFolder.Raw(captureDir, "clock_samples.csv");
        if (!File.Exists(clockPath)) return "no clock_samples.csv to place the Arma 3 camera on QPC";
        var clock = ClockMap.Fit(Csv.ReadFile(clockPath).Rows
            .Select(r => (Frames.ParseDouble(r["t_qpc_ms"]), Frames.ParseDouble(r["utc_unix_ms"]))).ToList());
        using (var w = new StreamWriter(CaptureFolder.Raw(captureDir, "camera_samples.csv")))
        {
            w.Write(Csv.Line(["t_qpc_ms", "pos_x", "pos_y", "pos_z", "fwd_x", "fwd_y", "fwd_z", "right_x", "right_y", "right_z",
                "up_x", "up_y", "up_z", "fov_deg"]));
            foreach (var r in rows)
            {
                double qpc = clock.QpcRefMs + (r.UtcMs - clock.UtcAtRefMs) / clock.Slope;
                w.Write(Csv.Line(new[] { qpc }
                    .Concat(new[] { r.Position, r.Forward, r.Right, r.Up }.SelectMany(v => new[] { v.X, v.Y, v.Z }))
                    .Append(r.VfovDeg).Select(Frames.FormatDouble)));
            }
        }
        File.WriteAllText(CaptureFolder.Raw(captureDir, "extractor.json"), """
            {
              "game": "Arma 3",
              "engine": "Real Virtuality 4",
              "convention": "rfu",
              "units_to_world": 1.0,
              "fov_axis": "vertical",
              "camera_model": "PINHOLE"
            }

            """);
        return null;
    }

    public static List<Arma3CameraRow> Read(string path)
    {
        var lines = File.ReadLines(path).Where(l => l.Length > 0).ToList();
        if (lines.Count == 0 || lines[0].Trim() != string.Join(",", Header))
            throw new InvalidDataException($"{path}: unexpected header");
        var rows = new List<Arma3CameraRow>(lines.Count - 1);
        for (int k = 1; k < lines.Count; k++)
        {
            var f = lines[k].Split(',');
            if (f.Length != Header.Length)
            {
                if (k == lines.Count - 1) break;  // cut short: the game was closed mid-round
                throw new InvalidDataException($"{path}: row with {f.Length} fields: {lines[k]}");
            }
            double D(int i) => Frames.ParseDouble(f[i]);
            Vec3 V(int i) => new(D(i), D(i + 1), D(i + 2));
            rows.Add(new Arma3CameraRow(UtcMs(f[0]), int.Parse(f[1], CultureInfo.InvariantCulture), V(2), V(5), V(8), V(11), D(14)));
        }
        return rows;
    }
}
