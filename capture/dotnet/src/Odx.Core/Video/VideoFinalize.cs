using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Odx.Core;

/// <summary>
/// Turns what odx record leaves after a round, the fragmented video-only raw/video_raw.mp4 and raw/audio.wav, into the
/// delivered video.mp4: the video stream copied as is (no re-encoding), the sound encoded to AAC, moov up front, and in
/// a room the round's frames only (<see cref="RoundFrames"/>: the grid starts at round_start on every machine, so cutting
/// at round_end leaves every player the same frames; document/video-alignment-plan.md §3.2). Files in, file out,
/// seconds of work, so a failure costs a retry, not the round: ffmpeg gets no pipe to wait on while recording
/// (document/record-finalize-plan.md). Run by CaptureRun once the recorder has exited and by odx build when video.mp4 is
/// missing; the recorder itself knows nothing about rounds.
/// </summary>
public static class VideoFinalize
{
    public const string RawVideo = "video_raw.mp4", AudioFile = "audio.wav", Output = "video.mp4";

    /// <param name="Frames">Video samples in the output (0 when ffmpeg failed).</param>
    /// <param name="Error">Null when the output has the expected frames (and a sound track when a WAV was given).</param>
    public sealed record Result(bool Ok, long Frames, bool HasAudio, long ElapsedMs, string? Error)
    {
        public JsonNode ToNode() => new JsonObject
        {
            ["ok"] = Ok, ["frames"] = Frames, ["has_audio"] = HasAudio, ["elapsed_ms"] = ElapsedMs, ["error"] = Error,
        };
    }

    /// <summary>The time allowed: a stream copy is disk-bound and AAC encodes far faster than real time.</summary>
    public static TimeSpan Limit(double durationSeconds) => TimeSpan.FromSeconds(Math.Max(60, 0.5 * durationSeconds));

    /// <summary>
    /// Writes <paramref name="output"/> from <paramref name="rawVideo"/> and <paramref name="audioWav"/> (skipped when null
    /// or missing), the first <paramref name="keepFrames"/> frames only when that is fewer than
    /// <paramref name="expectedFrames"/> (the rows of video_frames.csv); the sound is cut with them. The output is checked
    /// against the frames expected after the cut: fewer (ffmpeg killed while recording) still give a playable video.mp4,
    /// reported as not ok. The inputs are deleted once the output stands; on an ffmpeg failure or a time-out they stay for
    /// a retry and no output is left.
    /// </summary>
    /// <param name="progress">Frames muxed so far, as ffmpeg reports them (about twice a second).</param>
    public static Result Run(string ffmpeg, string rawVideo, string? audioWav, string output, long expectedFrames, double fps,
        Action<long>? progress = null, long? keepFrames = null)
    {
        var clock = Stopwatch.StartNew();
        bool audio = audioWav is not null && File.Exists(audioWav);
        long expected = keepFrames is long keep && keep < expectedFrames ? keep : expectedFrames;
        bool cut = expected < expectedFrames;
        string part = output + ".part";
        var args = new List<string> { "-y", "-loglevel", "error", "-nostdin", "-progress", "pipe:1", "-i", rawVideo };
        if (audio) args.AddRange(["-i", audioWav!]);
        args.AddRange(["-map", "0:v:0"]);
        if (audio) args.AddRange(["-map", "1:a:0", "-c:a", "aac", "-b:a", "160k"]);
        if (cut)
        {
            // No B-frames in the raw video: the first N packets are the first N frames.
            args.AddRange(["-frames:v", expected.ToString(CultureInfo.InvariantCulture)]);
            if (audio) args.AddRange(["-af", $"atrim=end={(expected / fps).ToString("F6", CultureInfo.InvariantCulture)}"]);
        }
        args.AddRange(["-c:v", "copy", "-movflags", "+faststart", "-f", "mp4", part]);
        var limit = Limit(fps > 0 ? expected / fps : 0);
        string? error = null;
        try
        {
            RunFfmpeg(ffmpeg, args, limit, progress);
            var info = Mp4Reader.Read(part);
            if (info.Frames != expected)
                error = $"{Path.GetFileName(output)} has {info.Frames} frames, expected {expected}" + (cut ? "" : " (video_frames.csv)");
            else if (audio && !info.HasAudio)
                error = $"{Path.GetFileName(output)} has no sound track";
            File.Move(part, output, overwrite: true);
            File.Delete(rawVideo);
            if (audio) File.Delete(audioWav!);
            return new Result(error is null, info.Frames, info.HasAudio, clock.ElapsedMilliseconds, error);
        }
        catch (Exception e) when (e is InvalidOperationException or IOException or InvalidDataException or UnauthorizedAccessException)
        {
            try
            {
                File.Delete(part);
            }
            catch (IOException)
            {
            }
            return new Result(false, 0, false, clock.ElapsedMilliseconds, e.Message);
        }
    }

    /// <summary>
    /// The frames a round's video has on every machine: grid ticks from round_start up to round_end, when recording.json
    /// carries the round's times and the grid's origin (CaptureRun writes them; a free-running grid has no origin and is
    /// not cut). Null otherwise.
    /// </summary>
    public static long? RoundFrames(JsonObject recording)
    {
        double? fps = Number(recording, "fps"), start = Number(recording, "round_start_utc_ms"), end = Number(recording, "round_end_utc_ms");
        if (fps is not > 0 || start is null || end is null || end <= start || Number(recording, "grid_origin_qpc_ms") is null) return null;
        return RoundFrames(start.Value, end.Value, fps.Value);
    }

    /// <summary>Grid ticks from round_start up to round_end: the frames a round's video has on every machine (odx merge counts the same way).</summary>
    public static long RoundFrames(double startUtcMs, double endUtcMs, double fps) => (long)Math.Ceiling((endUtcMs - startUtcMs) * fps / 1000 - 1e-6);

    /// <summary>
    /// Finalizes a capture folder whose video.mp4 is missing while video_raw.mp4 exists, cut to the round's frames when
    /// recording.json gives them (video_frames.csv is cut with it, and recording.json gets <c>finalize</c>,
    /// <c>frames</c>, <c>recorded_frames</c>, <c>tail_frames_cut</c>, <c>round_frames</c>); null when there is nothing to do.
    /// </summary>
    public static Result? RunIfNeeded(string captureDir, string ffmpeg, Action<long>? progress = null)
    {
        string output = Path.Combine(captureDir, Output);
        string? rawVideo = Find(captureDir, RawVideo);
        if (File.Exists(output) || rawVideo is null) return null;
        string framesCsv = Find(captureDir, "video_frames.csv") ?? "";
        long rows = File.Exists(framesCsv) ? CompleteRows(framesCsv) : 0;
        string? recordingPath = Find(captureDir, "recording.json");
        var recording = recordingPath is null ? null : TryRead(recordingPath);
        double fps = recording is null ? 0 : Number(recording, "fps") ?? 0;
        long? roundFrames = recording is null ? null : RoundFrames(recording);
        var result = Run(ffmpeg, rawVideo, Find(captureDir, AudioFile), output, rows, fps, progress, roundFrames);
        long kept = roundFrames is long n && n < rows ? n : rows;
        if (result.Ok && kept < rows && File.Exists(framesCsv)) CutCsv(framesCsv, kept);
        if (recordingPath is not null && recording is not null)
        {
            recording["finalize"] = result.ToNode();
            if (roundFrames is long round) recording["round_frames"] = round;
            if (result.Ok)
            {
                recording["frames"] = result.Frames;
                if (kept < rows)
                {
                    recording["recorded_frames"] = rows;
                    recording["tail_frames_cut"] = rows - kept;
                }
            }
            File.WriteAllText(recordingPath, recording.ToJsonString(Json));
        }
        return result;
    }

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static JsonObject? TryRead(string path)
    {
        try
        {
            return JsonNode.Parse(File.ReadAllText(path))?.AsObject();
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }

    private static double? Number(JsonObject o, string key) =>
        o[key] is JsonValue v && v.TryGetValue(out double d) ? d : null;

    /// <summary>The header and the first <paramref name="rows"/> data rows; the rest were after the round's end.</summary>
    private static void CutCsv(string csv, long rows)
    {
        var lines = File.ReadLines(csv).Take((int)rows + 1).ToList();
        File.WriteAllText(csv, string.Concat(lines.Select(l => l + "\n")));
    }

    /// <summary>At the top of the folder, else in raw/ (a tidied capture).</summary>
    private static string? Find(string dir, string name)
    {
        string top = Path.Combine(dir, name), raw = Path.Combine(dir, "raw", name);
        return File.Exists(top) ? top : File.Exists(raw) ? raw : null;
    }

    /// <summary>Data rows ending in a newline: a killed writer leaves a partial last row, which is no frame.</summary>
    private static long CompleteRows(string csv)
    {
        long rows = -1;  // the header
        using var reader = new StreamReader(csv);
        string? line;
        while ((line = reader.ReadLine()) is not null)
            if (line.Length > 0 && (!reader.EndOfStream || EndsWithNewline(csv))) rows++;
        return Math.Max(0, rows);
    }

    private static bool EndsWithNewline(string path)
    {
        using var file = File.OpenRead(path);
        if (file.Length == 0) return false;
        file.Position = file.Length - 1;
        return file.ReadByte() == '\n';
    }

    /// <exception cref="InvalidOperationException">ffmpeg failed, did not start or ran past <paramref name="limit"/> (killed).</exception>
    private static void RunFfmpeg(string ffmpeg, IEnumerable<string> args, TimeSpan limit, Action<long>? progress)
    {
        var psi = new ProcessStartInfo(ffmpeg)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true,
        };
        foreach (string a in args) psi.ArgumentList.Add(a);
        Process process;
        try
        {
            process = Process.Start(psi) ?? throw new InvalidOperationException("ffmpeg did not start");
        }
        catch (System.ComponentModel.Win32Exception e)
        {
            throw new InvalidOperationException($"cannot run {ffmpeg}: {e.Message}");
        }
        using (process)
        {
            var errors = process.StandardError.ReadToEndAsync();
            process.OutputDataReceived += (_, e) =>
            {
                if (e.Data is { } line && line.StartsWith("frame=", StringComparison.Ordinal) &&
                    long.TryParse(line[6..].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out long n))
                    progress?.Invoke(n);
            };
            process.BeginOutputReadLine();
            if (!process.WaitForExit(limit))
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit();
                throw new InvalidOperationException($"finalize did not finish within {limit.TotalSeconds:0} s; ffmpeg killed");
            }
            process.WaitForExit();  // flushes the redirected streams
            if (process.ExitCode != 0)
                throw new InvalidOperationException($"ffmpeg exited with {process.ExitCode}: {errors.Result.Trim()}");
        }
    }
}
