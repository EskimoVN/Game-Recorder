using System.Diagnostics;

namespace Odx.Capture;

/// <summary>
/// Eskimo: what a player's machine must pass before it says ready and before the host starts a round. The frame must be
/// exactly 16:9, and the game's keys must be the profile's: the capture maps each physical key to the profile's action,
/// so a key rebound in the game would record the wrong action. Only refuses "ready"; the recording itself is unchanged.
/// </summary>
public static class RecordingGate
{
    public const string Arma3Process = "arma3_x64";
    public const string ReforgerProcess = "ArmaReforgerSteam";

    /// <summary>The line the Eskimo Arma 3 mod writes into the .rpt every 30 s: "ok", or "bad," and the actions, ';'-separated.</summary>
    public const string Arma3KeysTag = "ESKIMO,keys,";

    /// <summary>The mod writes every 30 s, so the last 256 KB of the .rpt always hold its latest line.</summary>
    private const int RptTailBytes = 256 * 1024;

    public static bool Is16By9(int width, int height) => width > 0 && height > 0 && width * 9 == height * 16;

    /// <summary>"window:&lt;process&gt;" of a capture probe's source; null for a monitor.</summary>
    public static string? WindowProcess(string source) =>
        source.StartsWith("window:", StringComparison.Ordinal) ? source["window:".Length..] : null;

    /// <summary>The actions whose profile key no longer triggers them, from the last keys line; null when they all do or there is none.</summary>
    public static IReadOnlyList<string>? Arma3ReboundActions(string rptText)
    {
        int at = rptText.LastIndexOf(Arma3KeysTag, StringComparison.Ordinal);
        if (at < 0) return null;
        int start = at + Arma3KeysTag.Length;
        int end = rptText.IndexOfAny(['"', '\r', '\n'], start);
        string result = rptText[start..(end < 0 ? rptText.Length : end)];
        if (!result.StartsWith("bad,", StringComparison.Ordinal)) return null;
        var actions = result["bad,".Length..].Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return actions.Length > 0 ? actions : null;
    }

    /// <summary>
    /// Arma Reforger keeps a player's rebound keys and mouse buttons in InputUserSettings.conf, as keyboard: / mouse: inputs.
    /// </summary>
    public static bool ReforgerHasReboundKeys(string confText) =>
        confText.Contains("keyboard:", StringComparison.OrdinalIgnoreCase) ||
        confText.Contains("mouse:", StringComparison.OrdinalIgnoreCase);

    /// <summary>Arma 3's rebound actions on this machine: from the newest .rpt of the running game; null when none or unknown.</summary>
    public static IReadOnlyList<string>? Arma3ReboundActionsNow()
    {
        var started = ProcessStart(Arma3Process);
        if (started is null) return null;
        var dir = new DirectoryInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Arma 3"));
        if (!dir.Exists) return null;
        var rpt = dir.EnumerateFiles("*.rpt").Where(f => f.LastWriteTimeUtc >= started.Value)
            .OrderByDescending(f => f.LastWriteTimeUtc).FirstOrDefault();
        return rpt is null ? null : Arma3ReboundActions(ReadTail(rpt.FullName));
    }

    /// <summary>The InputUserSettings.conf with rebound keys under the OdxReforger profile, or null.</summary>
    public static string? ReforgerReboundFileNow()
    {
        var save = new DirectoryInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            "OdxReforger", "profile", ".save"));
        if (!save.Exists) return null;
        foreach (var conf in save.EnumerateFiles("InputUserSettings.conf", SearchOption.AllDirectories))
        {
            try
            {
                if (ReforgerHasReboundKeys(File.ReadAllText(conf.FullName))) return conf.FullName;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // unreadable while the game writes it: checked again on the next tick
            }
        }
        return null;
    }

    private static DateTime? ProcessStart(string name)
    {
        var running = Process.GetProcessesByName(name);
        try
        {
            return running.Select(p =>
            {
                try { return (DateTime?)p.StartTime.ToUniversalTime(); }
                catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception) { return null; }
            }).Where(t => t is not null).Max();
        }
        finally
        {
            foreach (var p in running) p.Dispose();
        }
    }

    private static string ReadTail(string path)
    {
        try
        {
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            file.Seek(Math.Max(0, file.Length - RptTailBytes), SeekOrigin.Begin);
            using var reader = new StreamReader(file);
            return reader.ReadToEnd();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return "";
        }
    }
}
