using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Odx.Agent;
using Odx.Capture;
using Odx.Core;
using Xunit;
using Xunit.Abstractions;

namespace Odx.Tests;

/// <summary>
/// The orchestrator under load, run by hand: ODX_LOAD=&lt;published odx-orchestrator.exe&gt; [ODX_LOAD_ROOMS=100]
/// [ODX_LOAD_ROUNDS=3] [ODX_LOAD_ROUND_S=20]. Rooms of two fake players (no capture, a copied session folder), one event a
/// second per room, and every player's clock sampled once a second, as real players do; prints the orchestrator's CPU and
/// memory and how long starts, stops and event posts take.
/// </summary>
public sealed class LoadTests(ITestOutputHelper output)
{
    [SkippableFact]
    public async Task Orchestrator_under_load()
    {
        string? exe = Environment.GetEnvironmentVariable("ODX_LOAD");
        Skip.If(exe is null, "set ODX_LOAD to a published odx-orchestrator.exe");
        int rooms = Env("ODX_LOAD_ROOMS", 100), rounds = Env("ODX_LOAD_ROUNDS", 3), roundS = Env("ODX_LOAD_ROUND_S", 20);
        string root = Cs2Fixtures.TempDir();
        string profile = Path.Combine(root, "game.json");
        File.WriteAllText(profile, """
            {"game_title": "Synthetic", "platform": "PC", "localization": "en-US",
             "input_mouse_convention": {"maps_to": "camera_look_velocity"}, "bindings": {}}
            """);
        string template = SessionFixtures.MakeValidSession(Path.Combine(root, "template"), video: false);
        const int httpPort = 5381, timePort = 12491;
        using var orchestrator = Process.Start(new ProcessStartInfo(exe!,
            ["--game-profile", profile, "--http-port", $"{httpPort}", "--time-port", $"{timePort}", "--state", Path.Combine(root, "state.db")])
            { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false })!;
        _ = orchestrator.StandardOutput.ReadToEndAsync();
        _ = orchestrator.StandardError.ReadToEndAsync();
        using var stop = new CancellationTokenSource();
        try
        {
            using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{httpPort}") };
            for (int i = 0; i < 100; i++)
            {
                try
                {
                    await http.GetAsync("/status");
                    break;
                }
                catch (HttpRequestException)
                {
                    await Task.Delay(100);
                }
            }

            var started = new System.Collections.Concurrent.ConcurrentDictionary<string, DateTime>();
            var stopped = new System.Collections.Concurrent.ConcurrentDictionary<string, DateTime>();
            var codes = new List<string>();
            for (int r = 0; r < rooms; r++)
            {
                string code = (string)(await (await http.PostAsync("/rooms", null)).Content.ReadFromJsonAsync<JsonObject>())!["code"]!;
                codes.Add(code);
                foreach (string p in new[] { $"r{r}a", $"r{r}b" })
                {
                    var agent = new AgentClient(new AgentOptions(http.BaseAddress, p, p, Path.Combine(root, p), null, RoomCode: code),
                        options =>
                        {
                            started[$"{p}/{options.DirName}"] = DateTime.UtcNow;
                            return new StopTimed(new OrchestratorTests.FakeCapture(options, template), () => stopped.TryAdd($"{p}/{options.DirName}", DateTime.UtcNow));
                        });
                    _ = agent.RunUntilStoppedAsync(stop.Token);
                }
            }
            for (int i = 0; i < 600; i++)
            {
                var status = (await http.GetFromJsonAsync<JsonObject>("/status"))!;
                if (status["rooms"]!.AsArray().Sum(r => r!["members"]!.AsArray().Count) == rooms * 2) break;
                await Task.Delay(100);
            }
            output.WriteLine($"{rooms * 2} agents in {rooms} rooms");

            _ = Task.Run(() => SampleClocks(timePort, rooms * 2, stop.Token));
            var cpu = new List<double>();
            long peakBytes = 0;
            _ = Task.Run(async () =>
            {
                var last = orchestrator.TotalProcessorTime;
                var at = DateTime.UtcNow;
                while (!stop.IsCancellationRequested)
                {
                    await Task.Delay(1000);
                    orchestrator.Refresh();
                    var now = DateTime.UtcNow;
                    lock (cpu) cpu.Add((orchestrator.TotalProcessorTime - last).TotalMilliseconds / (now - at).TotalMilliseconds * 100);
                    peakBytes = Math.Max(peakBytes, orchestrator.WorkingSet64);
                    (last, at) = (orchestrator.TotalProcessorTime, now);
                }
            });

            var posts = new List<double>();
            var startPosted = new Dictionary<string, DateTime>();
            var endPosted = new Dictionary<string, DateTime>();
            async Task Post(string code, object e)
            {
                var sw = Stopwatch.StartNew();
                (await http.PostAsJsonAsync($"/rooms/{code}/events", e)).EnsureSuccessStatusCode();
                lock (posts) posts.Add(sw.Elapsed.TotalMilliseconds);
            }
            for (int round = 1; round <= rounds; round++)
            {
                for (int r = 0; r < rooms; r++)
                    for (int i = 0; i < 600 && !Idle((await http.GetFromJsonAsync<JsonObject>($"/rooms/{codes[r]}"))!); i++) await Task.Delay(100);
                await Task.WhenAll(codes.Select(async (code, r) =>
                {
                    lock (startPosted) startPosted[$"m{r}-r{round:D2}"] = DateTime.UtcNow;
                    await Post(code, new { type = "round_start", match_id = $"m{r}" });
                }));
                for (int s = 0; s < roundS; s++)
                {
                    await Task.WhenAll(codes.Select((code, r) => Post(code, new { type = "kill", attacker = $"r{r}a", victim = $"r{r}b" })));
                    await Task.Delay(1000);
                }
                await Task.WhenAll(codes.Select(async (code, r) =>
                {
                    lock (endPosted) endPosted[$"m{r}-r{round:D2}"] = DateTime.UtcNow;
                    await Post(code, new { type = "round_end" });
                }));
                output.WriteLine($"round {round} done");
            }
            for (int r = 0; r < rooms; r++)
                for (int i = 0; i < 600 && !Idle((await http.GetFromJsonAsync<JsonObject>($"/rooms/{codes[r]}"))!); i++) await Task.Delay(100);
            stop.Cancel();

            var startLag = started.Select(s => (s.Value - startPosted[s.Key.Split('/')[1]]).TotalMilliseconds).ToList();
            var stopLag = stopped.Select(s => (s.Value - endPosted[s.Key.Split('/')[1]]).TotalMilliseconds).ToList();
            var finished = (await http.GetFromJsonAsync<JsonObject>("/status"))!["rooms"]!.AsArray()
                .Sum(r => r!["sessions"]!.AsArray().Count(s => (int)s!["kept"]! == 2));
            output.WriteLine($"rounds kept by both players: {finished} of {rooms * rounds}");
            output.WriteLine($"orchestrator CPU (% of one core): mean {cpu.Average():F1}, p95 {P(cpu, 95):F1}, max {cpu.Max():F1}; " +
                             $"peak memory {peakBytes / 1048576} MB");
            output.WriteLine($"event POST ms: p50 {P(posts, 50):F1}, p95 {P(posts, 95):F1}, max {posts.Max():F1} ({posts.Count} posts)");
            output.WriteLine($"round_start -> capture started, ms: p50 {P(startLag, 50):F0}, p95 {P(startLag, 95):F0}, max {startLag.Max():F0}");
            output.WriteLine($"round_end -> capture stopped, ms: p50 {P(stopLag, 50):F0}, p95 {P(stopLag, 95):F0}, max {stopLag.Max():F0}");
            Assert.Equal(rooms * rounds, finished);
        }
        finally
        {
            stop.Cancel();
            orchestrator.Kill(true);
        }
    }

    private static bool Idle(JsonObject room) =>
        room["current_session"] is null && room["pending_sessions"]!.AsArray().Count == 0 &&
        room["members"]!.AsArray().All(m => (string?)m!["state"] == "idle");

    /// <summary>One SNTP query a second per player, spread over the second.</summary>
    private static async Task SampleClocks(int timePort, int players, CancellationToken stop)
    {
        while (!stop.IsCancellationRequested)
        {
            var second = Task.Delay(1000);
            await Parallel.ForAsync(0, players, new ParallelOptions { MaxDegreeOfParallelism = 16 }, (_, _) =>
            {
                try
                {
                    SntpClient.Query($"127.0.0.1:{timePort}", TimeSpan.FromSeconds(2));
                }
                catch (Exception e) when (e is System.Net.Sockets.SocketException or InvalidDataException)
                {
                }
                return ValueTask.CompletedTask;
            });
            await second;
        }
    }

    private static double P(List<double> values, int percent)
    {
        var sorted = values.Order().ToList();
        return sorted[Math.Min(sorted.Count - 1, sorted.Count * percent / 100)];
    }

    private static int Env(string name, int fallback) =>
        int.TryParse(Environment.GetEnvironmentVariable(name), out int v) ? v : fallback;

    private sealed class StopTimed(ICaptureRun inner, Action stopped) : ICaptureRun
    {
        public CapturePhase Phase => CapturePhase.Recording;
        public int FinalizePercent => 0;
        public event Action? PhaseChanged { add { } remove { } }
        public string SessionDir => inner.SessionDir;
        public Task Started => inner.Started;
        public Task CaptureStopped => inner.CaptureStopped;
        public Task<RunResult> Completion => inner.Completion;

        public void Stop()
        {
            stopped();
            inner.Stop();
        }
    }
}
