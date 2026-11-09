using System.Diagnostics;
using System.Globalization;

namespace Odx.Core;

/// <summary>ffmpeg over a recording: a frame-accurate cut of its head, and the count of frames that decode.</summary>
public static class VideoTrim
{
    /// <summary>
    /// Writes <paramref name="src"/> from frame <paramref name="startFrame"/> on to <paramref name="dst"/>, re-encoded:
    /// odx record's keyframes are 2–4 s apart, so a copy cut would land anywhere in a 2–3 s pre-roll. The video keeps its
    /// frame rate frame for frame (timestamps rebased, no CFR resampling that could add or drop one); the audio, if any,
    /// is cut at the same instants and encoded again.
    /// </summary>
    /// <param name="endFrame">The first frame left out; null keeps the video to its end.</param>
    /// <exception cref="InvalidOperationException">ffmpeg failed; the message has its stderr.</exception>
    public static void Trim(string ffmpeg, string src, string dst, int startFrame, double fps, bool hasAudio, int? endFrame = null)
    {
        var inv = CultureInfo.InvariantCulture;
        string end = endFrame is int e ? $":end_frame={e.ToString(inv)}" : "";
        string audioEnd = endFrame is int ea ? $":end={(ea / fps).ToString("F9", inv)}" : "";
        var args = new List<string>
        {
            "-y", "-loglevel", "error", "-i", src,
            "-vf", $"trim=start_frame={startFrame.ToString(inv)}{end},setpts=PTS-STARTPTS",
        };
        if (hasAudio) args.AddRange(["-af", $"atrim=start={(startFrame / fps).ToString("F9", inv)}{audioEnd},asetpts=PTS-STARTPTS"]);
        args.AddRange(["-c:v", "libx264", "-preset", "veryfast", "-crf", "18", "-bf", "0", "-g", "120", "-pix_fmt", "yuv420p",
                       "-fps_mode", "passthrough"]);
        args.AddRange(hasAudio ? ["-c:a", "aac", "-b:a", "160k"] : ["-an"]);
        args.AddRange(["-movflags", "+faststart", dst]);
        Run(ffmpeg, args, stdout: false);
    }

    /// <summary>How many frames of the first video stream decode (ffprobe -count_frames without ffprobe: ~10–20 s per 5 min of 1080p60).</summary>
    /// <exception cref="InvalidOperationException">ffmpeg failed or reported no frames.</exception>
    public static long DecodedFrameCount(string ffmpeg, string path)
    {
        string output = Run(ffmpeg, ["-v", "error", "-i", path, "-map", "0:v:0", "-fps_mode", "passthrough", "-f", "null",
                                     "-progress", "pipe:1", "-"], stdout: true);
        long? frames = null;
        foreach (string line in output.Split('\n'))
            if (line.StartsWith("frame=", StringComparison.Ordinal) && long.TryParse(line[6..].Trim(), out long n)) frames = n;
        return frames ?? throw new InvalidOperationException($"ffmpeg reported no frame count for {path}");
    }

    private static string Run(string ffmpeg, IEnumerable<string> args, bool stdout)
    {
        var psi = new ProcessStartInfo(ffmpeg) { UseShellExecute = false, RedirectStandardError = true, RedirectStandardOutput = stdout };
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
            string output = stdout ? process.StandardOutput.ReadToEnd() : "";
            process.WaitForExit();
            if (process.ExitCode != 0) throw new InvalidOperationException($"ffmpeg exited with {process.ExitCode}: {errors.Result.Trim()}");
            return output;
        }
    }
}
