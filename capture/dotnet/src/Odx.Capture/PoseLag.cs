using System.Diagnostics;
using Odx.Core;

namespace Odx.Capture;

/// <summary>
/// How far the game's camera pose runs ahead of the picture it ends up in (TIM-05/06): per window, the lag that best
/// lines up the camera's rotation per frame with how much the picture changes, as research/arma-sync-test/analyze.py does.
/// </summary>
public static class PoseLag
{
    public const int Width = 64, Height = 36;
    public const double WindowS = 10;
    /// <summary>Windows whose correlation peak is weaker than this are left out (nothing moved).</summary>
    public const double MinCorrelation = 0.3;
    private const int Span = 6;

    /// <param name="LagFrames">Median over the usable windows; + the pose is ahead of the picture.</param>
    /// <param name="SpreadFrames">95th percentile of the usable windows' distance from the median.</param>
    public sealed record Result(double LagFrames, double SpreadFrames, int Windows, int Usable);

    /// <summary>Null when no window has enough movement to measure.</summary>
    public static Result? Measure(IReadOnlyList<double> cameraMotion, IReadOnlyList<double> pictureChange, double fps)
    {
        int n = Math.Min(cameraMotion.Count, pictureChange.Count);
        int win = Math.Max(1, (int)Math.Round(WindowS * fps));
        var lags = new List<double>();
        int windows = 0;
        for (int w0 = 0; w0 < Math.Max(1, n - win + 1); w0 += win)
        {
            windows++;
            int len = Math.Min(win, n - w0);
            if (len <= 2 * Span) continue;
            var (lag, r) = Peak(cameraMotion.Skip(w0).Take(len).ToArray(), pictureChange.Skip(w0).Take(len).ToArray());
            if (r >= MinCorrelation) lags.Add(lag);
        }
        if (lags.Count == 0) return null;
        double median = Median(lags);
        var spread = lags.Select(l => Math.Abs(l - median)).Order().ToList();
        return new Result(median, spread[(int)Math.Ceiling(0.95 * spread.Count) - 1], windows, lags.Count);
    }

    /// <summary>Yaw + pitch change per frame (rad) of canonical C2W poses; a frame without a pose counts as no change.</summary>
    public static List<double> CameraMotion(IReadOnlyList<double[,]?> c2w)
    {
        var motion = new List<double>(c2w.Count);
        double yaw = 0, pitch = 0;
        foreach (var m in c2w)
        {
            if (m is null) { motion.Add(0); continue; }
            // Columns are right | up | backward | translation, world RFU: forward is −column 2.
            double fx = -m[0, 2], fy = -m[1, 2], fz = -m[2, 2];
            double y = Math.Atan2(fx, fy), p = Math.Asin(Math.Clamp(fz, -1, 1));
            double dyaw = Math.IEEERemainder(y - yaw, 2 * Math.PI);
            motion.Add(motion.Count == 0 ? 0 : Math.Abs(dyaw) + Math.Abs(p - pitch));
            (yaw, pitch) = (y, p);
        }
        return motion;
    }

    /// <summary>
    /// Mean absolute grey change per frame of the video at 64×36, for frame logs without picture_change (before 0.3.12);
    /// null when ffmpeg cannot read it (the bundled one has no H.264 decoder: such rounds are rebuilt with a full ffmpeg).
    /// </summary>
    public static List<double>? PictureChange(string video)
    {
        if (!File.Exists(video)) return null;
        var psi = new ProcessStartInfo(ExternalTools.Ffmpeg())
        {
            UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true,
        };
        foreach (var a in new[] { "-v", "error", "-i", video, "-vf", $"scale={Width}:{Height},format=gray", "-f", "rawvideo", "-" })
            psi.ArgumentList.Add(a);
        byte[] raw;
        try
        {
            using var p = Process.Start(psi)!;
            var err = p.StandardError.ReadToEndAsync();
            using var bytes = new MemoryStream();
            p.StandardOutput.BaseStream.CopyTo(bytes);
            p.WaitForExit();
            _ = err.Result;
            if (p.ExitCode != 0) return null;
            raw = bytes.ToArray();
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return null;
        }
        const int size = Width * Height;
        var change = new List<double>(raw.Length / size);
        for (int f = 0; f + size <= raw.Length; f += size)
        {
            if (f == 0) { change.Add(0); continue; }
            long sum = 0;
            for (int i = 0; i < size; i++) sum += Math.Abs(raw[f + i] - raw[f - size + i]);
            change.Add((double)sum / size);
        }
        return change;
    }

    private static double Corr(ReadOnlySpan<double> a, ReadOnlySpan<double> b)
    {
        int n = a.Length;
        double ma = 0, mb = 0;
        for (int i = 0; i < n; i++) { ma += a[i]; mb += b[i]; }
        ma /= n; mb /= n;
        double va = 0, vb = 0, cov = 0;
        for (int i = 0; i < n; i++)
        {
            va += (a[i] - ma) * (a[i] - ma);
            vb += (b[i] - mb) * (b[i] - mb);
            cov += (a[i] - ma) * (b[i] - mb);
        }
        return va == 0 || vb == 0 ? 0 : cov / Math.Sqrt(va * vb);
    }

    /// <summary>Lag k (frames, sub-frame by a parabola) maximising corr(a[i], b[i + k]), and the correlation there.</summary>
    private static (double Lag, double R) Peak(double[] a, double[] b)
    {
        var c = new Dictionary<int, double>();
        for (int k = -Span; k <= Span; k++)
            c[k] = k >= 0 ? Corr(a.AsSpan(0, a.Length - k), b.AsSpan(k)) : Corr(a.AsSpan(-k), b.AsSpan(0, b.Length + k));
        int best = c.MaxBy(x => x.Value).Key;
        if (best == -Span || best == Span) return (best, c[best]);
        double y0 = c[best - 1], y1 = c[best], y2 = c[best + 1], d = y0 - 2 * y1 + y2;
        return (best + (d != 0 ? 0.5 * (y0 - y2) / d : 0), y1);
    }

    private static double Median(List<double> values)
    {
        var s = values.Order().ToList();
        return s.Count % 2 == 1 ? s[s.Count / 2] : (s[s.Count / 2 - 1] + s[s.Count / 2]) / 2;
    }
}
