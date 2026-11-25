using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using Odx.Core;
using Odx.Protocol;

namespace Odx.Capture;

public enum QuickLevel { Ok, Warn, Fail }

/// <param name="Id">fps, size, freeze, audio_track, silence, clipping, input_keys, input_mouse, files.</param>
/// <param name="Value">The measured value, for the message.</param>
public sealed record QuickItem(string Id, QuickLevel Level, string Value);

/// <summary>
/// Eskimo: the team's checks of a finished round (frame, sound, timing in ms, input, files), run on the player's machine once
/// the round is kept, in a few seconds (no video decoding: the mp4's header, the recorder's counters, frames.csv, file sizes
/// and one audio-only ffmpeg pass). Only reported; the verdict the room and the server use stays CaptureReport's.
/// </summary>
public static partial class RoundQuickCheck
{
    public const double Fps = 30, FpsTolerance = 0.05;
    /// <summary>A peak at full scale is the clipping the team's checker reports.</summary>
    public const double ClipDb = -0.1;
    /// <summary>A stretch under −50 dB this long is the long silence the team's checker reports.</summary>
    public const double LongSilenceSeconds = 10;

    /// <summary>
    /// The delivered files (CaptureFolder.Delivered) but events.jsonl: the agent writes it once the whole room is done
    /// (AgentClient.KeepRoundFiles), after this check runs.
    /// </summary>
    public static readonly IReadOnlyList<string> RequiredFiles = [.. CaptureFolder.Delivered.Where(n => n != "events.jsonl")];

    public static IReadOnlyList<QuickItem> Run(RunResult result, string ffmpeg) =>
        [.. Recording(result, ffmpeg), .. Input(result.SessionDir), Files(result.SessionDir)];

    /// <summary>
    /// Frames with a key and frames with mouse movement, counted as the company's merge counts them for frame_stats
    /// (MultiplayerSession.AddFloorChecks); a round with none of either recorded no keyboard or no mouse.
    /// </summary>
    public static IEnumerable<QuickItem> Input(IEnumerable<IReadOnlyDictionary<string, string>> rows)
    {
        int keys = 0, mouse = 0;
        foreach (var r in rows)
        {
            if (r.GetValueOrDefault("input_keys", Frames.Null) != Frames.Null) keys++;
            if (r.GetValueOrDefault("input_mouse_dx", Frames.Null) is not ("0.0" or Frames.Null) ||
                r.GetValueOrDefault("input_mouse_dy", Frames.Null) is not ("0.0" or Frames.Null)) mouse++;
        }
        yield return Count("input_keys", keys);
        yield return Count("input_mouse", mouse);
    }

    /// <summary>Input of the round's frames.csv; a missing or unreadable one counts as no input.</summary>
    public static IEnumerable<QuickItem> Input(string sessionDir)
    {
        string path = Path.Combine(sessionDir, "frames.csv");
        List<Dictionary<string, string>> rows = [];
        if (File.Exists(path))
        {
            try
            {
                rows = Csv.ReadFile(path).Rows;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { /* counted as no input */ }
        }
        return Input(rows);
    }

    /// <summary>Every required file at the top of the round folder and not empty; Value names the ones that are not.</summary>
    public static QuickItem Files(string sessionDir)
    {
        var missing = RequiredFiles.Where(name => new FileInfo(Path.Combine(sessionDir, name)) is not { Exists: true, Length: > 0 }).ToList();
        return missing.Count == 0 ? new QuickItem("files", QuickLevel.Ok, "ok")
            : new QuickItem("files", QuickLevel.Fail, string.Join(", ", missing));
    }

    private static QuickItem Count(string id, int frames) =>
        new(id, frames > 0 ? QuickLevel.Ok : QuickLevel.Fail, frames.ToString(CultureInfo.InvariantCulture));

    private static List<QuickItem> Recording(RunResult result, string ffmpeg)
    {
        var items = new List<QuickItem>();
        string video = Path.Combine(result.SessionDir, VideoFinalize.Output);
        if (!File.Exists(video)) return [new QuickItem("size", QuickLevel.Fail, "no video.mp4")];
        Mp4Info info;
        try
        {
            info = Mp4Reader.Read(video);
        }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            return [new QuickItem("size", QuickLevel.Fail, e.Message)];
        }
        items.AddRange(Video(info, result.Report?.MaxSourceGapMs));
        if (!info.HasAudio)
        {
            items.Add(new QuickItem("audio_track", QuickLevel.Fail, "0"));
            return items;
        }
        items.Add(new QuickItem("audio_track", QuickLevel.Ok, "1"));
        items.AddRange(Audio(AudioPass(ffmpeg, video), result.Report?.AudioSilentRatio));
        return items;
    }

    public static IEnumerable<QuickItem> Video(Mp4Info info, double? maxGapMs)
    {
        double fps = info.DurationSeconds > 0 ? info.Frames / info.DurationSeconds : 0;
        yield return new QuickItem("fps", Math.Abs(fps - Fps) <= FpsTolerance ? QuickLevel.Ok : QuickLevel.Fail, Fmt(fps, "F2"));
        bool size = RecordingGate.Is16By9(info.Width, info.Height) && info.Width >= 1280;
        yield return new QuickItem("size", size ? QuickLevel.Ok : QuickLevel.Fail, $"{info.Width}x{info.Height}");
        if (maxGapMs is double gap)
            yield return new QuickItem("freeze", gap > CaptureReport.FrozenMs ? QuickLevel.Fail
                : gap >= CaptureReport.FrozenDegradedMs ? QuickLevel.Warn : QuickLevel.Ok, Fmt(gap, "F0"));
    }

    /// <param name="ffmpegLog">ffmpeg's stderr after silencedetect and volumedetect; null when it could not run.</param>
    public static IEnumerable<QuickItem> Audio(string? ffmpegLog, double? silentRatio)
    {
        bool allSilent = silentRatio > CaptureReport.MaxSilentRatio;
        if (allSilent) yield return new QuickItem("silence", QuickLevel.Fail, Fmt(100 * silentRatio!.Value, "F0") + "%");
        if (ffmpegLog is null) yield break;
        double longest = SilenceRegex().Matches(ffmpegLog).Select(m => Num(m.Groups[1].Value) ?? 0).DefaultIfEmpty(0).Max();
        if (!allSilent)
            yield return new QuickItem("silence", longest >= LongSilenceSeconds ? QuickLevel.Warn : QuickLevel.Ok, Fmt(longest, "F1"));
        // A track of silence only says "-inf": no number, and the silence item already tells it.
        if (MaxRegex().Match(ffmpegLog) is { Success: true } max && Num(max.Groups[1].Value) is double peak)
        {
            yield return new QuickItem("clipping", peak >= ClipDb ? QuickLevel.Warn : QuickLevel.Ok, Fmt(peak, "F1"));
        }
    }

    private static string? AudioPass(string ffmpeg, string video)
    {
        var start = new ProcessStartInfo(ffmpeg) { RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (string a in new[] { "-hide_banner", "-nostats", "-i", video, "-vn", "-af",
                     $"silencedetect=n=-50dB:d={LongSilenceSeconds.ToString(CultureInfo.InvariantCulture)},volumedetect", "-f", "null", "-" })
            start.ArgumentList.Add(a);
        try
        {
            using var process = Process.Start(start);
            if (process is null) return null;
            var log = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(30_000))
            {
                process.Kill();
                return null;
            }
            return log.Result;
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return null;
        }
    }

    private static double? Num(string s) => double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double v) ? v : null;

    private static string Fmt(double v, string format) => v.ToString(format, CultureInfo.InvariantCulture);

    [GeneratedRegex(@"silence_duration: ([0-9.]+)")]
    private static partial Regex SilenceRegex();

    [GeneratedRegex(@"max_volume: (-?[0-9.]+) dB")]
    private static partial Regex MaxRegex();
}
