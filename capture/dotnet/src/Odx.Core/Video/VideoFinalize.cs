using System.Diagnostics;
using System.Globalization;
using System.Text.Json.Nodes;

namespace Odx.Core;

/// <summary>
/// Turns what odx record leaves after a round, the fragmented video-only raw/video_raw.mp4 and raw/audio.wav, into the
/// delivered video.mp4: the video stream copied as is (no re-encoding), the sound encoded to AAC, moov up front. Files
/// in, file out, seconds of work, so a failure costs a retry, not the round: ffmpeg gets no pipe to wait on while
/// recording (document/record-finalize-plan.md). Run by odx record at the end of a recording, by CaptureRun when the
/// recorder was killed before it could, and by odx build when video.mp4 is missing.
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
    /// or missing). The output is checked against <paramref name="expectedFrames"/> (the rows of video_frames.csv): fewer
    /// frames (ffmpeg killed while recording) still give a playable video.mp4, reported as not ok. The inputs are deleted
    /// once the output stands; on an ffmpeg failure or a time-out they stay for a retry and no output is left.
    /// </summary>
    /// <param name="progress">Frames muxed so far, as ffmpeg reports them (about twice a second).</param>
    public static Result Run(string ffmpeg, string rawVideo, string? audioWav, string output, long expectedFrames, double fps,
        Action<long>? progress = null)
    {
        var clock = Stopwatch.StartNew();
        bool audio = audioWav is not null && File.Exists(audioWav);
        string part = output + ".part";
        var args = new List<string> { "-y", "-loglevel", "error", "-nostdin", "-progress", "pipe:1", "-i", rawVideo };
        if (audio) args.AddRange(["-i", audioWav!]);
        args.AddRange(["-map", "0:v:0"]);
        if (audio) args.AddRange(["-map", "1:a:0", "-c:a", "aac", "-b:a", "160k"]);
        args.AddRange(["-c:v", "copy", "-movflags", "+faststart", "-f", "mp4", part]);
        var limit = Limit(fps > 0 ? expectedFrames / fps : 0);
        string? error = null;
        try
        {
            RunFfmpeg(ffmpeg, args, limit, progress);
            var info = Mp4Reader.Read(part);
            if (info.Frames != expectedFrames)
                error = $"{Path.GetFileName(output)} has {info.Frames} frames, video_frames.csv {expectedFrames}";
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

    /// <summary>Finalizes a capture folder whose video.mp4 is missing while video_raw.mp4 exists; null when there is nothing to do.</summary>
    public static Result? RunIfNeeded(string captureDir, string ffmpeg, Action<long>? progress = null)
    {
        string output = Path.Combine(captureDir, Output);
        string? rawVideo = Find(captureDir, RawVideo);
        if (File.Exists(output) || rawVideo is null) return null;
        string framesCsv = Find(captureDir, "video_frames.csv") ?? "";
        long frames = File.Exists(framesCsv) ? CompleteRows(framesCsv) : 0;
        double fps = 0;
        if (Find(captureDir, "recording.json") is { } recording)
        {
            try
            {
                fps = JsonNode.Parse(File.ReadAllText(recording))?["fps"]?.GetValue<double>() ?? 0;
            }
            catch (System.Text.Json.JsonException)
            {
            }
        }
        return Run(ffmpeg, rawVideo, Find(captureDir, AudioFile), output, frames, fps, progress);
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
