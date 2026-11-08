using System.Diagnostics;
using System.Globalization;
using System.Text.Json.Nodes;
using Odx.Core;

namespace Odx.Tests;

/// <summary>One valid reference session plus mutators that each break one v2 §1.5 rule.</summary>
public static class SessionFixtures
{
    public const int Fps = 60, NFrames = 120, Width = 320, Height = 180;

    /// <summary>Video fixtures and video checks need ffmpeg + ffprobe on PATH; without them those tests are skipped.</summary>
    public static readonly bool HasFfmpeg = Runs("ffmpeg") && Runs("ffprobe");

    private static bool Runs(string tool)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo(tool, "-version")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            })!;
            p.StandardOutput.ReadToEnd();
            p.WaitForExit();
            return p.ExitCode == 0;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    /// <param name="audio">Also a 440 Hz AAC track (48 kHz stereo), as odx record --audio writes one.</param>
    public static void MakeVideo(string path, int nFrames = NFrames, int width = Width, int height = Height, int fps = Fps, bool audio = false)
    {
        var psi = new ProcessStartInfo("ffmpeg") { UseShellExecute = false, RedirectStandardError = true };
        string[] audioIn = audio ? ["-f", "lavfi", "-i", "sine=frequency=440:sample_rate=48000"] : [];
        string[] audioOut = audio ? ["-ac", "2", "-c:a", "aac", "-shortest"] : [];
        string[] args = ["-v", "error", "-y", "-f", "lavfi", "-i", $"testsrc=size={width}x{height}:rate={fps}", .. audioIn,
                         "-frames:v", nFrames.ToString(CultureInfo.InvariantCulture), "-c:v", "libx264", "-bf", "0",
                         "-pix_fmt", "yuv420p", .. audioOut, path];
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        string err = p.StandardError.ReadToEnd();
        p.WaitForExit();
        if (p.ExitCode != 0) throw new InvalidOperationException($"ffmpeg failed: {err}");
    }

    public static string MakeValidSession(string dir, bool video = true)
    {
        Directory.CreateDirectory(dir);
        double durationMs = NFrames * 1000.0 / Fps;
        var session = new JsonObject
        {
            ["vendor_name"] = "odx",
            ["game_title"] = "synthetic",
            ["session_id"] = "fixture-0001",
            ["created_at_utc"] = "2026-10-04T10:00:00Z",
            ["ended_at_utc"] = "2026-10-04T10:00:02Z",
            ["duration_ms"] = durationMs,
            ["duration_seconds"] = durationMs / 1000,
            ["fps"] = Fps,
            ["frame_count"] = NFrames,
            ["record_width_px"] = Width,
            ["record_height_px"] = Height,
            ["screen_width_px"] = 1920,
            ["screen_height_px"] = 1080,
            ["localization"] = "en-US",
            ["platform"] = "PC",
            ["input_mouse_convention"] = new JsonObject
            {
                ["maps_to"] = "camera_look_velocity",
                ["dx_positive"] = "right",
                ["dx_negative"] = "left",
                ["dy_positive"] = "down",
                ["dy_negative"] = "up",
            },
        };
        File.WriteAllText(Path.Combine(dir, "session.json"), session.ToJsonString());

        var times = Enumerable.Range(0, NFrames).Select(i => Math.Round(i * 1000.0 / Fps)).ToList();
        var events = new List<InputEvent>
        {
            new(times[10] - 1, InputKind.KeyDown, "W"),
            new(times[10] - 1, InputKind.ActionStart, "Forward"),
            new(times[40] - 1, InputKind.KeyUp, "W"),
            new(times[40] - 1, InputKind.ActionEnd, "Forward"),
        };
        events.AddRange(Enumerable.Range(50, 30).Select(i => new InputEvent(times[i] - 2, InputKind.MouseMove, "", 3, -1)));
        var inputs = Frames.AggregateInputs(times, events);
        var intrinsics = Geometry.IntrinsicsFromVfov(60, Width, Height);
        Frames.WriteCsv(Path.Combine(dir, "frames.csv"), times.Select((t, i) => new FrameRow(i, (long)t,
            Geometry.C2wFromForward(Geometry.ForwardFromHeadingPitch(i * 0.75, 5), new Vec3(i * 0.1, 0, 1.7)),
            "PINHOLE", intrinsics, inputs[i])));
        if (video) MakeVideo(Path.Combine(dir, "video.mp4"));
        return dir;
    }

    public static string CopySession(string src, string dst)
    {
        Directory.CreateDirectory(dst);
        foreach (var f in Directory.GetFiles(src)) File.Copy(f, Path.Combine(dst, Path.GetFileName(f)));
        return dst;
    }

    public static void EditSession(string dir, Action<JsonObject> edit)
    {
        string path = Path.Combine(dir, "session.json");
        var s = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        edit(s);
        File.WriteAllText(path, s.ToJsonString());
    }

    /// <summary><paramref name="edit"/> changes rows in place and may return a new header (e.g. to drop a column).</summary>
    public static void EditRows(string dir, Func<List<Dictionary<string, string>>, List<string>?> edit)
    {
        string path = Path.Combine(dir, "frames.csv");
        var (header, rows) = Csv.ReadFile(path);
        header = edit(rows) ?? header;
        File.WriteAllText(path, Csv.Line(header) + string.Concat(rows.Select(r => Csv.Line(header.Select(h => r[h])))));
    }

    public static void EditRows(string dir, Action<List<Dictionary<string, string>>> edit) =>
        EditRows(dir, rows => { edit(rows); return null; });

    private static string Repr(double v) => Frames.FormatDouble(v);
    private static double Num(string s) => Frames.ParseDouble(s);
    private static int KeyFrame(List<Dictionary<string, string>> rows) => rows.FindIndex(r => r["input_keys"] != Frames.Null);
    private static int NoInputFrame(List<Dictionary<string, string>> rows) =>
        rows.FindIndex(r => r["input_keys"] == Frames.Null && r["input_mouse_dx"] == "0.0");

    /// <summary>Rule id → mutator(session dir). Each targets exactly that rule (others may fire as a consequence).</summary>
    public static readonly Dictionary<string, Action<string>> Mutations = new()
    {
        ["session.schema"] = d => EditSession(d, s => s.Remove("platform")),
        ["session.timezone"] = d => EditSession(d, s => s["created_at_utc"] = "2026-10-04T10:00:00"),
        ["session.end_after_start"] = d => EditSession(d, s => s["ended_at_utc"] = "2026-10-04T09:59:58Z"),
        ["session.duration_units"] = d => EditSession(d, s => s["duration_seconds"] = 4.0),
        ["session.duration_timestamps"] = d => EditSession(d, s => s["ended_at_utc"] = "2026-10-04T10:00:12Z"),
        ["session.frame_count_fps"] = d => EditSession(d, s => s["fps"] = 30),
        ["frames.headers"] = d => EditRows(d, _ => Frames.Columns.Where(c => c != "input_mouse_buttons").ToList()),
        ["frames.row_count"] = d => EditSession(d, s => s["frame_count"] = NFrames - 1),
        ["frames.frame_id_sequence"] = d => EditRows(d, rows => rows[5]["frame_id"] = "7"),
        ["frames.timestamp_monotonic"] = d => EditRows(d, rows => rows[50]["timestamp_ms"] = rows[49]["timestamp_ms"]),
        ["frames.timestamp_start"] = d => EditRows(d, rows => rows.ForEach(r => r["timestamp_ms"] = (long.Parse(r["timestamp_ms"]) + 100).ToString())),
        ["frames.timestamp_end"] = d => EditSession(d, s => { s["duration_ms"] = 1900.0; s["duration_seconds"] = 1.9; }),
        ["frames.frame_interval"] = d => EditRows(d, rows => rows[60]["timestamp_ms"] = (long.Parse(rows[60]["timestamp_ms"]) + 8).ToString()),
        ["camera.sparse_nulls"] = d => EditRows(d, rows => { foreach (var c in Frames.C2wColumns) rows[30][c] = Frames.Null; }),
        ["camera.model"] = d => EditRows(d, rows => rows[0]["camera_model"] = "PERSPECTIVE"),
        ["camera.principal_point"] = d => EditRows(d, rows => rows[0]["camera_cx"] = Repr(Num(rows[0]["camera_cx"]) + 20)),
        ["camera.fx_fy"] = d => EditRows(d, rows => rows[0]["camera_fx"] = Repr(Num(rows[0]["camera_fx"]) * 1.1)),
        ["c2w.bottom_row"] = d => EditRows(d, rows => rows[0]["C2W_M30"] = "0.1"),
        ["c2w.det"] = d => EditRows(d, rows =>
        {
            foreach (var c in new[] { "C2W_M02", "C2W_M12", "C2W_M22" }) rows[20][c] = Repr(-Num(rows[20][c]));
        }),
        ["c2w.orthonormal"] = d => EditRows(d, rows => rows[0]["C2W_M00"] = Repr(Num(rows[0]["C2W_M00"]) * 1.5)),
        ["input.mouse_delta"] = d => EditRows(d, rows => rows[3]["input_mouse_dx"] = Frames.Null),
        ["input.keys_without_actions"] = d => EditRows(d, rows => rows[KeyFrame(rows)]["input_actions"] = Frames.Null),
        ["input.buttons_without_actions"] = d => EditRows(d, rows => rows[NoInputFrame(rows)]["input_mouse_buttons"] = "X1"),
        ["input.no_input_null"] = d => EditRows(d, rows => rows[NoInputFrame(rows)]["input_actions"] = "Jump"),
        ["input.delimiter"] = d => EditRows(d, rows => rows[KeyFrame(rows)]["input_keys"] = "W,Shift"),
        ["video.missing"] = d => File.Delete(Path.Combine(d, "video.mp4")),
        ["video.frame_count"] = d => MakeVideo(Path.Combine(d, "video.mp4"), nFrames: NFrames - 10),
        ["video.resolution"] = d => MakeVideo(Path.Combine(d, "video.mp4"), width: 640, height: 360),
        ["video.duration"] = d => MakeVideo(Path.Combine(d, "video.mp4"), fps: Fps / 2),  // same frames, twice as long
    };
}
