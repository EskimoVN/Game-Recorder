// odx validate <session_dir> [<session_dir> ...] [--json] [--no-video] [--decode]
// A session dir holds session.json, frames.csv and video.mp4. Exit code 1 if any error is found; warnings do not fail.
using System.Text.Encodings.Web;
using System.Text.Json;
using Odx.Capture;

namespace Odx.Cli;

public static class ValidateCommand
{
    public static int Run(string[] args)
    {
        bool json = args.Contains("--json"), video = !args.Contains("--no-video"), decode = args.Contains("--decode");
        var sessions = args.Where(a => !a.StartsWith("--", StringComparison.Ordinal)).ToList();
        if (sessions.Count == 0 || args.Any(a => a.StartsWith("--", StringComparison.Ordinal) && a is not ("--json" or "--no-video" or "--decode")))
        {
            Console.Error.WriteLine("usage: odx validate <session_dir> [<session_dir> ...] [--json] [--no-video] [--decode]");
            return 2;
        }

        var results = sessions.ToDictionary(s => s, s => SessionValidator.ValidateSession(s, video, decode: decode));
        if (json)
        {
            var options = new JsonSerializerOptions
            {
                WriteIndented = true,
                PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
                Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            };
            Console.WriteLine(JsonSerializer.Serialize(results, options));
        }
        else
        {
            foreach (var (session, issues) in results)
            {
                int errors = issues.Count(i => i.Severity == "error");
                Console.WriteLine($"{(errors > 0 ? "FAIL" : "PASS")}  {session}  ({errors} errors, {issues.Count - errors} warnings)");
                foreach (var i in issues) Console.WriteLine($"  [{i.Severity}] {i.Rule}: {i.Message}");
            }
        }
        return results.Values.Any(issues => issues.Any(i => i.Severity == "error")) ? 1 : 0;
    }
}
