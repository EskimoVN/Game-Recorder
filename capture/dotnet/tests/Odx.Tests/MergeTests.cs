using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using Odx.Agent;
using Odx.Capture;
using Odx.Games.Cs2;
using Odx.Merge;
using Odx.Orchestrator;
using Odx.Protocol;
using Xunit;

namespace Odx.Tests;

/// <summary>
/// The game server agent under a real orchestrator, against a fake RCON and a game directory written by hand (no demo:
/// SourceTV "does not record").
/// </summary>
public sealed class GameServerAgentTests
{
    [Fact]
    public async Task Every_round_records_the_servers_side_and_its_clock_and_keeps_them_for_merge()
    {
        string root = Cs2Fixtures.TempDir(), game = Path.Combine(root, "csgo");
        string logs = Path.Combine(game, "odx-netlog");
        Directory.CreateDirectory(logs);
        Directory.CreateDirectory(Path.Combine(game, "replays"));
        var lines = File.ReadAllLines(Cs2Fixtures.NetLog);
        File.WriteAllLines(Path.Combine(logs, "a.jsonl"), lines[..10]);
        string profile = Path.Combine(root, "game.json");
        File.WriteAllText(profile, """
            {"game_title": "Counter-Strike 2", "platform": "PC", "localization": "en-US",
             "input_mouse_convention": {"maps_to": "camera_look_velocity"}, "bindings": {}}
            """);
        await using var app = OrchestratorHost.Build(new OrchestratorOptions(profile, HttpPort: 0, TimePort: 0), "127.0.0.1");
        await app.StartAsync();
        using var http = new HttpClient { BaseAddress = new Uri(app.Urls.First()) };
        using var rcon = new FakeRcon("pw");
        using var stop = new CancellationTokenSource();
        var agent = new GameServerAgent(new GameServerAgentOptions(new Uri(app.Urls.First()), Path.Combine(root, "work")),
            new Cs2Server(new Cs2ServerOptions("127.0.0.1", rcon.Port, "pw", game), NullLogger.Instance), NullLogger.Instance);
        _ = agent.RunUntilStoppedAsync(stop.Token);
        await LocalRoundTests.WaitForGameServers(http, 1);

        string code = (string)(await (await http.PostAsync("/rooms", null)).Content.ReadFromJsonAsync<JsonObject>())!["code"]!;
        (await http.PostAsJsonAsync($"/rooms/{code}/events", new { type = "round_start", match_id = "g1" })).EnsureSuccessStatusCode();
        for (int i = 0; i < 100 && !rcon.Commands.Contains("tv_record replays/g1-r01"); i++) await Task.Delay(50);
        File.AppendAllLines(Path.Combine(logs, "a.jsonl"), lines[10..20]);
        (await http.PostAsJsonAsync($"/rooms/{code}/events", new { type = "round_end" })).EnsureSuccessStatusCode();

        string kept = Path.Combine(root, "work", "g1-r01");
        Assert.True(await LocalRoundTests.WaitForFile(Path.Combine(kept, RoundRecord.FileName)));
        stop.Cancel();
        var (dir, report) = MultiplayerSession.Assemble(kept, Path.Combine(root, "out"), Odx.Cli.MergeCommand.Vendor,
            games: Odx.Cli.MergeCommand.Games);
        Assert.Equal(["tv_record replays/g1-r01", "tv_stoprecord"], rcon.Commands);
        Assert.Equal(lines[10..20], File.ReadAllLines(Path.Combine(root, "out", MultiplayerSession.WorkDir, "g1-r01", "server", Cs2ServerCapture.NetLogFile)));
        Assert.True(File.Exists(Path.Combine(dir, "server", PacketLog.BinFile)));
        var server = JsonNode.Parse(File.ReadAllText(Path.Combine(dir, "session.json")))!["server"]!;
        Assert.True(server["clock_sync"]!["sample_count"]!.GetValue<int>() >= 2);  // at least the round's start and end
        Assert.InRange(server["clock_sync"]!["offset_ms"]!.GetValue<double>(), -50, 50);  // the same machine
        Assert.Contains(report.Issues, i => i.Rule == "server.capture" && i.Message.StartsWith("SourceTV is not recording", StringComparison.Ordinal));
    }
}

public sealed class LocalRoundTests
{
    internal static async Task<bool> WaitForFile(string path)
    {
        for (int i = 0; i < 200 && !File.Exists(path); i++) await Task.Delay(50);
        return File.Exists(path);
    }

    internal static async Task WaitForGameServers(HttpClient http, int count)
    {
        for (int i = 0; i < 100 && (await http.GetFromJsonAsync<JsonObject>("/status"))!["game_servers"]!.AsArray().Count < count; i++)
            await Task.Delay(50);
    }

    [Fact]
    public async Task Two_rooms_record_at_once_each_on_its_own_game_server()
    {
        string root = Cs2Fixtures.TempDir();
        string profile = Path.Combine(root, "game.json");
        File.WriteAllText(profile, """
            {"game_title": "Counter-Strike 2", "platform": "PC", "localization": "en-US",
             "input_mouse_convention": {"maps_to": "camera_look_velocity"}, "bindings": {}}
            """);
        await using var app = OrchestratorHost.Build(new OrchestratorOptions(profile, HttpPort: 0, TimePort: 0), "127.0.0.1");
        await app.StartAsync();
        var orchestrator = new Uri(app.Urls.First());
        using var http = new HttpClient { BaseAddress = orchestrator };
        using var stop = new CancellationTokenSource();
        string[] servers = ["s1", "s2"];
        var rcons = servers.ToDictionary(s => s, _ => new FakeRcon("pw"));
        foreach (string s in servers)
        {
            string game = Path.Combine(root, s, "csgo");
            Directory.CreateDirectory(Path.Combine(game, "odx-netlog"));
            Directory.CreateDirectory(Path.Combine(game, "replays"));
            _ = new GameServerAgent(new GameServerAgentOptions(orchestrator, Path.Combine(root, s, "work"), Id: s),
                new Cs2Server(new Cs2ServerOptions("127.0.0.1", rcons[s].Port, "pw", game), NullLogger.Instance), NullLogger.Instance)
                .RunUntilStoppedAsync(stop.Token);
        }
        await WaitForGameServers(http, 2);

        var rooms = new Dictionary<string, string>();
        foreach (string s in servers)
            rooms[s] = (string)(await (await http.PostAsync($"/rooms?server={s}", null)).Content.ReadFromJsonAsync<JsonObject>())!["code"]!;
        foreach (string s in servers)
            (await http.PostAsJsonAsync($"/rooms/{rooms[s]}/events", new { type = "round_start", match_id = "m" + s })).EnsureSuccessStatusCode();
        foreach (string s in servers)
            for (int i = 0; i < 100 && !rcons[s].Commands.Contains($"tv_record replays/m{s}-r01"); i++) await Task.Delay(50);
        foreach (string s in servers)
            (await http.PostAsJsonAsync($"/rooms/{rooms[s]}/events", new { type = "round_end" })).EnsureSuccessStatusCode();

        foreach (string s in servers)
        {
            Assert.True(await WaitForFile(Path.Combine(root, s, "work", $"m{s}-r01", RoundRecord.FileName)), s);
            Assert.Equal([$"m{s}-r01"], Directory.GetDirectories(Path.Combine(root, s, "work")).Select(Path.GetFileName));
            Assert.Equal([$"tv_record replays/m{s}-r01", "tv_stoprecord"], rcons[s].Commands);
            Assert.Equal(s, (string?)(await http.GetFromJsonAsync<JsonObject>($"/rooms/{rooms[s]}"))!["server"]);
        }
        stop.Cancel();
        foreach (var rcon in rcons.Values) rcon.Dispose();
    }

    [Fact]
    public void Merge_refuses_a_folder_without_sessions()
    {
        string root = Cs2Fixtures.TempDir();
        Assert.Equal(2, Odx.Cli.MergeCommand.Run([]));
        Assert.Equal(2, Odx.Cli.MergeCommand.Run([Path.Combine(root, "missing")]));
        Assert.Equal(2, Odx.Cli.MergeCommand.Run([root]));
        Assert.Equal(2, Odx.Cli.MergeCommand.Run([root, "--out"]));
    }

    [Fact]
    public async Task Two_players_and_the_game_server_each_keep_a_folder_of_the_staging_layout()
    {
        string root = Cs2Fixtures.TempDir(), game = Path.Combine(root, "csgo");
        Directory.CreateDirectory(Path.Combine(game, "odx-netlog"));
        Directory.CreateDirectory(Path.Combine(game, "replays"));
        string template = SessionFixtures.MakeValidSession(Path.Combine(root, "template"), video: false);
        string profile = Path.Combine(root, "game.json");
        File.WriteAllText(profile, """
            {"game_title": "Synthetic", "platform": "PC", "localization": "en-US",
             "input_mouse_convention": {"maps_to": "camera_look_velocity"}, "bindings": {}}
            """);
        await using var app = OrchestratorHost.Build(new OrchestratorOptions(profile, HttpPort: 0, TimePort: 0), "127.0.0.1");
        await app.StartAsync();
        var orchestrator = new Uri(app.Urls.First());
        using var http = new HttpClient { BaseAddress = orchestrator };
        using var rcon = new FakeRcon("pw");
        using var stop = new CancellationTokenSource();
        var server = new GameServerAgent(new GameServerAgentOptions(orchestrator, Path.Combine(root, "server")),
            new Cs2Server(new Cs2ServerOptions("127.0.0.1", rcon.Port, "pw", game), NullLogger.Instance), NullLogger.Instance);
        _ = server.RunUntilStoppedAsync(stop.Token);
        await LocalRoundTests.WaitForGameServers(http, 1);

        string code = (string)(await (await http.PostAsync("/rooms", null)).Content.ReadFromJsonAsync<JsonObject>())!["code"]!;
        string[] players = ["p1", "p2"];
        foreach (string p in players)
            _ = new AgentClient(new AgentOptions(orchestrator, p, p, Path.Combine(root, "captures-" + p), null, RoomCode: code),
                options => new OrchestratorTests.FakeCapture(options, template)).RunAsync(stop.Token);
        for (int i = 0; i < 100 && (await http.GetFromJsonAsync<JsonObject>($"/rooms/{code}"))!["members"]!.AsArray()
                 .Count(m => (string?)m!["state"] == "idle") < players.Length; i++)
            await Task.Delay(50);

        (await http.PostAsJsonAsync($"/rooms/{code}/events", new { type = "round_start", match_id = "g1" })).EnsureSuccessStatusCode();
        for (int i = 0; i < 100 && !rcon.Commands.Contains("tv_record replays/g1-r01"); i++) await Task.Delay(50);
        (await http.PostAsJsonAsync($"/rooms/{code}/events", new { type = "round_end" })).EnsureSuccessStatusCode();

        string serverKept = Path.Combine(root, "server", "g1-r01");
        var kept = players.Select(p => Path.Combine(root, "captures-" + p, "g1-r01")).Append(serverKept).ToList();
        // A player's round files are in raw/ (CaptureFolder); the game server keeps them at the top of its folder.
        var roundFiles = kept.SkipLast(1).Select(d => Path.Combine(d, CaptureFolder.RawDir)).Append(serverKept).ToList();
        foreach (string dir in roundFiles) Assert.True(await WaitForFile(Path.Combine(dir, RoundRecord.FileName)), dir);
        foreach (string dir in roundFiles.SkipLast(1)) Assert.True(await WaitForFile(Path.Combine(dir, Manifest.FileName)), dir);
        stop.Cancel();

        foreach (string dir in roundFiles)
        {
            Assert.True(File.Exists(Path.Combine(dir, RoundRecord.EventsFileName)));
            Assert.True(File.Exists(Path.Combine(dir, RoundRecord.ProfileFileName)));
            Assert.Equal(File.ReadAllText(Path.Combine(serverKept, RoundRecord.FileName)), File.ReadAllText(Path.Combine(dir, RoundRecord.FileName)));
        }
        foreach (string dir in kept.SkipLast(1))
        {
            foreach (string f in new[] { "session.json", "frames.csv", "events.jsonl", "raw/video_frames.csv" })
                Assert.True(File.Exists(Path.Combine(dir, f)), Path.Combine(dir, f));
            Assert.All(File.ReadLines(Path.Combine(dir, "events.jsonl")), l => Assert.NotNull(JsonNode.Parse(l)!["local_receive_time_us"]));
            var listed = JsonNode.Parse(File.ReadAllText(Path.Combine(dir, CaptureFolder.RawDir, Manifest.FileName)))!["files"]!.AsArray()
                .Select(f => f!.AsObject()).ToList();
            Assert.Equal(Directory.GetFiles(dir, "*", SearchOption.AllDirectories).Length - 1, listed.Count);
            Assert.Contains(listed, f => (string?)f["path"] == $"{CaptureFolder.RawDir}/{RoundRecord.FileName}");
            Assert.All(listed, f =>
            {
                byte[] bytes = File.ReadAllBytes(Path.Combine(dir, (string)f["path"]!));
                Assert.Equal(bytes.Length, (long)f["bytes"]!);
                Assert.Equal(Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes)), (string?)f["sha256"]);
            });
        }
        Assert.True(File.Exists(Path.Combine(serverKept, "server", Cs2ServerCapture.ClockSamplesFile)));
        var round = RoundRecord.Load(serverKept);
        Assert.Equal(players, round.Players.Select(p => p.PlayerId).Order());
        Assert.All(round.Players, p => Assert.True(p.Done));
        Assert.Contains("round_end", File.ReadAllText(Path.Combine(serverKept, RoundRecord.EventsFileName)));
    }
}

public sealed class GameMergerTests
{
    [Fact]
    public void Odx_merge_knows_counter_strike_2_by_its_profile_title()
    {
        Assert.Equal(["Counter-Strike 2"], Odx.Cli.MergeCommand.Games.Servers.Select(g => g.Game));
        Assert.IsType<Cs2Merger>(Odx.Cli.MergeCommand.Games.Servers.Single());
    }
}
