using System.Globalization;

namespace Odx.Core;

/// <summary>
/// One line per event in <c>&lt;dir&gt;/odx-app-yyyyMMdd.log</c>: what a player sends when a round went wrong (the window
/// keeps only its last status line). Files older than <see cref="KeepDays"/> go when one is opened. Writing never throws:
/// a log that cannot be written is not worth failing a recording for.
/// </summary>
public sealed class DailyLogFile
{
    public const int KeepDays = 14;
    private const string Prefix = "odx-app-", Suffix = ".log";

    private readonly string _dir;
    private readonly object _gate = new();

    public DailyLogFile(string dir)
    {
        _dir = dir;
        Prune(DateTime.Now);
    }

    public void Write(string line)
    {
        var now = DateTime.Now;
        lock (_gate)
        {
            try
            {
                Directory.CreateDirectory(_dir);
                File.AppendAllText(Path.Combine(_dir, FileName(now)), $"{now:yyyy-MM-dd HH:mm:ss.fff} {line}{Environment.NewLine}");
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    public static string FileName(DateTime day) => $"{Prefix}{day.ToString("yyyyMMdd", CultureInfo.InvariantCulture)}{Suffix}";

    private void Prune(DateTime now)
    {
        if (!Directory.Exists(_dir)) return;
        var oldest = now.Date.AddDays(-KeepDays);
        foreach (string file in Directory.EnumerateFiles(_dir, $"{Prefix}*{Suffix}"))
        {
            string stamp = Path.GetFileName(file)[Prefix.Length..^Suffix.Length];
            if (!DateTime.TryParseExact(stamp, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day) || day >= oldest)
                continue;
            try
            {
                File.Delete(file);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
            }
        }
    }
}
