using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Odx.Agent;
using Odx.Capture;
using Odx.Cli;
using Odx.Core;
using Odx.Merge;
using Odx.Orchestrator;
using Odx.Protocol;
using Xunit;

namespace Odx.Tests;

/// <summary>
/// Orchestrator + odx-merge + real AgentClients over loopback; the capture is faked with a copy of the fixture session.
/// </summary>
public sealed class OrchestratorTests : IAsyncLifetime
{
    internal sealed class FakeCapture : ICaptureRun
    {
        private readonly TaskCompletionSource _stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <param name="building">How long the capture takes to build after it is stopped.</param>
        public FakeCapture(CaptureOptions options, string? template, TimeSpan building = default)
        {
            SessionDir = Path.Combine(options.OutputRoot, options.DirName ?? Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(SessionDir);
            if (template is not null)
            {
                SessionFixtures.CopySession(template, SessionDir);
                double now = NtpResponder.NowUnixMs();
                File.WriteAllLines(Path.Combine(SessionDir, "video_frames.csv"),
                    ["frame_index,t_grid_qpc_ms,t_source_qpc_ms,duplicate",
                     .. Enumerable.Range(0, SessionFixtures.NFrames).Select(i =>
                         string.Create(CultureInfo.InvariantCulture, $"{i},{1000 + i * 1000.0 / SessionFixtures.Fps},{1000 + i * 1000.0 / SessionFixtures.Fps},0"))]);
                File.WriteAllLines(Path.Combine(SessionDir, "ntp_samples.csv"),
                    [NtpSampleWriter.Header,
                     string.Create(CultureInfo.InvariantCulture, $"1000,{now},1,0,{options.NtpServer}"),
                     string.Create(CultureInfo.InvariantCulture, $"2000,{now + 1000},1,0,{options.NtpServer}")]);
            }
            Completion = _stopped.Task.ContinueWith(async _ =>
            {
                await Task.Delay(building);
                CaptureFolder.Tidy(SessionDir);  // as CaptureRun leaves the folder
                return new RunResult(SessionDir, template is not null, null, 0, [], null);
            }).Unwrap();
        }

        public string SessionDir { get; }
        public Task Started => Task.CompletedTask;
        public Task CaptureStopped => _stopped.Task;
        public Task<RunResult> Completion { get; }
        public void Stop() => _stopped.TrySetResult();
    }

    // The fixture session is 320x180 and 2 s long.
    private static readonly GateTolerances FixtureTolerances =
        GateTolerances.Default with { MinWidth = SessionFixtures.Width, MinHeight = SessionFixtures.Height, MinDurationMs = 1000 };

    private readonly string _root = Path.Combine(Path.GetTempPath(), "odx-orch-" + Guid.NewGuid().ToString("N"));
    private readonly CancellationTokenSource _stop = new();
    private WebApplication _app = null!;
    private HttpClient _http = null!;
    private MatchCoordinator _coordinator = null!;
    /// <summary>A real (well, local) NTP server for the orchestrator and the agents: spec v3 §4 layer 1.</summary>
    private UdpClient _ntp = null!;
    private string _ntpServer = null!;
    private readonly List<AgentClient> _agents = [];
    /// <summary>A room without a host (as a game server plugin opens one); most tests use it.</summary>
    private string _room = null!;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_root);
        string profile = Path.Combine(_root, "game.json");
        File.WriteAllText(profile, """
            {"game_title": "Synthetic", "platform": "PC", "localization": "en-US",
             "input_mouse_convention": {"maps_to": "camera_look_velocity"}, "bindings": {"W": "Forward"}}
            """);
        _ntp = NtpResponder.Bind(0);
        _ = NtpResponder.RunAsync(_ntp, _stop.Token);
        _ntpServer = $"127.0.0.1:{((IPEndPoint)_ntp.Client.LocalEndPoint!).Port}";
        _app = OrchestratorHost.Build(new OrchestratorOptions(profile, HttpPort: 0, TimePort: 0, Fps: SessionFixtures.Fps) { TimeServer = _ntpServer },
            "127.0.0.1");
        await _app.StartAsync();
        _http = new HttpClient { BaseAddress = new Uri(_app.Urls.First()) };
        _coordinator = _app.Services.GetRequiredService<MatchCoordinator>();
        using var opened = await _http.PostAsync("/rooms", null);
        _room = (string)(await opened.Content.ReadFromJsonAsync<JsonObject>())!["code"]!;
    }

    public async Task DisposeAsync()
    {
        _stop.Cancel();
        _ntp.Dispose();
        await _app.StopAsync();
        await _app.DisposeAsync();
        try
        {
            Directory.Delete(_root, true);
        }
        catch (IOException)
        {
        }
    }

    private Task<(string Dir, SessionReport Report)> NextSession()
    {
        var tcs = new TaskCompletionSource<(string, SessionReport)>(TaskCreationOptions.RunContinuationsAsynchronously);
        _coordinator.RoundRecorded += files => Task.Run(() =>
        {
            string staging = Path.Combine(_root, "test", files.SessionId);
            Directory.CreateDirectory(staging);
            files.WriteTo(staging);
            foreach (var p in RoundRecord.Load(staging).Players)
                if (Directory.Exists(Path.Combine(_root, "agent-" + p.PlayerId, files.SessionId)))
                    CopyDir(Path.Combine(_root, "agent-" + p.PlayerId, files.SessionId), Path.Combine(staging, "players", p.PlayerId));
            try
            {
                tcs.TrySetResult(MultiplayerSession.Assemble(MergeCommand.Sessions(staging).Single(), Path.Combine(_root, "out"),
                    MergeCommand.Vendor, FixtureTolerances));
            }
            catch (Exception e)
            {
                tcs.TrySetException(e);
            }
        });
        return tcs.Task.WaitAsync(TimeSpan.FromSeconds(30));
    }

    /// <summary>Agents joining the hostless test room.</summary>
    private List<TaskCompletionSource> ConnectAgents(string? template, params string[] players) =>
        players.Select(p => Connect(template, p, _room).Started).ToList();

    /// <param name="room">Invite code to join; null opens a new room with this player as host.</param>
    /// <param name="timeServer">The NTP server the agent checks its clock against before being ready; null skips the check.</param>
    private (AgentClient Agent, TaskCompletionSource Started) Connect(string? template, string player, string? room, string? timeServer = null,
        TimeSpan building = default)
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var agent = new AgentClient(new AgentOptions(_http.BaseAddress!, player, player.ToUpperInvariant(),
            Path.Combine(_root, "agent-" + player), null, RoomCode: room, CreateRoom: room is null, TimeServer: timeServer), options =>
        {
            try
            {
                return template is null ? throw new InvalidOperationException("no game window") : new FakeCapture(options, template, building);
            }
            finally
            {
                started.TrySetResult();
            }
        });
        _agents.Add(agent);
        _ = agent.RunAsync(_stop.Token);
        return (agent, started);
    }

    /// <summary>Opens a room hosted by <paramref name="host"/> and returns its invite code.</summary>
    private async Task<(AgentClient Host, TaskCompletionSource Started, string Code)> HostRoom(string host)
    {
        var (agent, started) = Connect(null, host, null);
        for (int i = 0; i < 100 && agent.Room is null; i++) await Task.Delay(50);
        return (agent, started, agent.Room?.Code ?? throw new TimeoutException("no room opened"));
    }

    private async Task WaitForAgents(int count, string? room = null)
    {
        for (int i = 0; i < 100; i++)
        {
            var status = await _http.GetFromJsonAsync<JsonObject>($"/rooms/{room ?? _room}");
            if (status!["members"]!.AsArray().Count(a => (string?)a!["state"] == "idle") == count) return;
            await Task.Delay(50);
        }
        throw new TimeoutException("agents did not join");
    }

    private Task<JsonObject> RoomStatus(string? room = null) => _http.GetFromJsonAsync<JsonObject>($"/rooms/{room ?? _room}")!;

    private Task Post(object e) =>
        _http.PostAsJsonAsync($"/rooms/{_room}/events", e).ContinueWith(t => t.Result.EnsureSuccessStatusCode());

    [SkippableFact]
    public async Task RoundBecomesV3SessionWithPerPlayerStopsAndFrameIndexedEvents()
    {
        Skip.IfNot(SessionFixtures.HasFfmpeg, "needs ffmpeg + ffprobe");
        string template = SessionFixtures.MakeValidSession(Path.Combine(_root, "template"));
        File.WriteAllText(Path.Combine(template, SessionBuilder.KeyBindingsFile), "{}");  // FakeCapture skips SessionBuilder
        var started = ConnectAgents(template, "p1", "p2");
        await WaitForAgents(2);
        var session = NextSession();

        await Post(new { type = "match_start", match_id = "m1", map = "de_dust2" });
        await Post(new { type = "round_start", countdown_ms = 1000 });
        await Task.WhenAll(started.Select(s => s.Task)).WaitAsync(TimeSpan.FromSeconds(10));
        await Post(new { type = "kill", attacker = "p1", victim = "p2", weapon = "ak47" });
        await Post(new { type = "player_death", player = "p2", permanent = true });
        await Task.Delay(1200);  // past the 1 s countdown: a round_end during it would cancel the round
        await Post(new { type = "round_end", winner = "t" });

        var (dir, report) = await session;
        Assert.True(report.Pass, string.Join("; ", report.Players.SelectMany(p => p.Issues).Select(i => i.Message)));
        Assert.Equal("m1-r01", report.SessionId);
        Assert.Equal(2, report.ValidPlayers);
        Assert.EndsWith(Path.Combine(MergeCommand.Vendor, DateTime.UtcNow.ToString("MM-dd-yyyy", CultureInfo.InvariantCulture), "synthetic", "m1-r01"), dir);

        var serverEvents = MultiplayerSession.ReadEvents(Path.Combine(dir, "server", "events.jsonl")).Select(GameEvents.Type);
        Assert.Equal(["match_start", "round_countdown", "kill", "player_death", "round_start", "round_end"], serverEvents);
        Assert.Equal("permanent_death", (string?)JsonNode.Parse(File.ReadAllText(Path.Combine(dir, "players", "p2", "player.json")))!["stop_reason"]);
        var p1 = JsonNode.Parse(File.ReadAllText(Path.Combine(dir, "players", "p1", "player.json")))!;
        Assert.Equal("round_end", (string?)p1["stop_reason"]);
        // The fake's 2 s of frames start when it is created, right after the round_start with a 1 s countdown: that
        // pre-roll is cut from the delivered stream, so play starts at frame 0 (spec v3 §1).
        Assert.InRange((int)p1["pre_roll_frames"]!, 30, 61);
        Assert.InRange((double)p1["pre_roll_ms"]!, 600, 1000);
        Assert.Equal(0, (int)p1["round_start_frame"]!);
        Assert.InRange((double)p1["round_start_video_time_ms"]!, -20, 0);
        Assert.Equal(SessionFixtures.NFrames - (int)p1["pre_roll_frames"]!, (int)p1["recording"]!["frame_count"]!);
        Assert.Contains(p1["qa"]!["checks"]!.AsArray(), c => (string?)c!["id"] == "video.trimmed" && (bool)c["pass"]!);
        Assert.Equal(2, (int)p1["clock_sync"]!["sample_count"]!);
        Assert.Equal("server_clock - player_clock", (string?)p1["clock_sync"]!["offset_definition"]);
        foreach (string p in new[] { "p1", "p2" })
        {
            Assert.True(File.Exists(Path.Combine(dir, "players", p, "video.mp4")));
            Assert.False(Directory.Exists(Path.Combine(dir, "players", p, "capture")));  // working files stay out of the delivery
            Assert.True(File.Exists(Path.Combine(_root, "out", MultiplayerSession.WorkDir, report.SessionId, "players", p, "session.json")));
            Assert.Empty(DeliveryFiles.Unexpected(dir));
            Assert.Equal(Mp4Reader.Read(Path.Combine(dir, "players", p, "video.mp4")).Frames, File.ReadLines(Path.Combine(dir, "players", p, "frames.csv")).Count() - 1);
            Assert.True(File.Exists(Path.Combine(dir, "players", p, SessionBuilder.KeyBindingsFile)));
            var events = MultiplayerSession.ReadEvents(Path.Combine(dir, "players", p, "events.jsonl"));
            var kill = events.Single(e => GameEvents.Type(e) == "kill");
            Assert.Null(kill["frame_index"]);  // posted during the countdown: before the delivered stream, which starts at round_start
            Assert.True((double)kill["video_time_ms"]! < 0);
            Assert.Equal("m1-r01", (string?)kill["session_id"]);
            Assert.NotNull(kill["event_id"]);
            var countdown = events.Single(e => GameEvents.Type(e) == "round_countdown");
            Assert.Null(countdown["frame_index"]);  // before the first frame: kept, with a negative video time
            Assert.True((double)countdown["video_time_ms"]! < 0);
            var player = JsonNode.Parse(File.ReadAllText(Path.Combine(dir, "players", p, "player.json")))!;
            var roundStart = events.Single(e => GameEvents.Type(e) == "round_start");
            // Frame 0 is the first frame at or after round_start, so the event itself falls within one frame before it.
            Assert.Equal(0, (int)player["round_start_frame"]!);
            Assert.Null(roundStart["frame_index"]);
            Assert.InRange((double)roundStart["video_time_ms"]!, -1000.0 / SessionFixtures.Fps, 0);

            var (header, frames) = FramesBin.Read(Path.Combine(dir, "players", p, FramesBin.FileName));
            Assert.Equal("odx.capture.v1.FrameRecord", header.RecordType);
            Assert.Equal(SessionFixtures.NFrames - (int)player["pre_roll_frames"]!, frames.Count);  // the pre-roll is cut
            var (_, rows) = Csv.ReadFile(Path.Combine(dir, "players", p, "frames.csv"));
            Assert.Equal(frames.Count, rows.Count);
            Assert.Equal("0", rows[0]["frame_id"]);
            Assert.Equal("0", rows[0]["timestamp_ms"]);
            Assert.Equal(Frames.ParseDouble(rows[40]["C2W_M03"]), frames[40].C2W[3]);
            Assert.True(frames[1].ServerTimeUs > frames[0].ServerTimeUs);

            Assert.True((bool)player["qa"]!["stream_valid"]!);
            Assert.Contains(player["qa"]!["checks"]!.AsArray(), c => (string?)c!["id"] == "floor.fps" && (bool)c["pass"]!);
            Assert.Equal("Forward", (string?)player["key_bindings"]!["W"]);
        }
        using (var gzip = new System.IO.Compression.GZipStream(File.OpenRead(Path.Combine(dir, FramesBin.SchemaFileName)),
                   System.IO.Compression.CompressionMode.Decompress))
        {
            var schema = Google.Protobuf.Reflection.FileDescriptorSet.Parser.ParseFrom(gzip);
            Assert.Equal("odx.capture.v1", schema.File.Single().Package);
            Assert.Contains(schema.File.Single().MessageType, m => m.Name == "FrameRecord");
        }
    }

    [SkippableFact]
    public async Task PlayersOwnCopiesOfTheRoundFilesMergeToTheSameSession()
    {
        Skip.IfNot(SessionFixtures.HasFfmpeg, "needs ffmpeg + ffprobe");
        string template = SessionFixtures.MakeValidSession(Path.Combine(_root, "template"));
        var started = ConnectAgents(template, "p1", "p2");
        await WaitForAgents(2);
        var session = NextSession();
        await Post(new { type = "match_start", match_id = "m4" });
        await Post(new { type = "round_start" });
        await Task.WhenAll(started.Select(s => s.Task)).WaitAsync(TimeSpan.FromSeconds(10));
        await Post(new { type = "kill", attacker = "p1", victim = "p2", weapon = "ak47" });
        await Post(new { type = "round_end" });
        var (assembled, report) = await session;
        Assert.True(report.Pass, string.Join("; ", report.Players.SelectMany(p => p.Issues).Select(i => i.Message)));

        string staging = Path.Combine(_root, "copied", report.SessionId);
        foreach (string p in new[] { "p1", "p2" })
        {
            string kept = Path.Combine(_root, "agent-" + p, report.SessionId);
            Assert.True(await LocalRoundTests.WaitForFile(Path.Combine(kept, CaptureFolder.RawDir, RoundRecord.FileName)));
            CopyDir(kept, Path.Combine(staging, "players", p));
            foreach (string f in new[] { RoundRecord.FileName, RoundRecord.EventsFileName, RoundRecord.ProfileFileName })
                File.Copy(Path.Combine(kept, CaptureFolder.RawDir, f), Path.Combine(staging, f), true);
        }
        Assert.Equal([staging], MergeCommand.Sessions(Path.Combine(_root, "copied")));
        var (merged, local) = MultiplayerSession.Assemble(staging, Path.Combine(_root, "local"), MergeCommand.Vendor, FixtureTolerances);

        Assert.True(local.Pass);
        Assert.Equal(report.ValidPlayers, local.ValidPlayers);
        var files = Directory.GetFiles(assembled, "*", SearchOption.AllDirectories).Select(f => Path.GetRelativePath(assembled, f)).Order().ToList();
        Assert.Equal(files, Directory.GetFiles(merged, "*", SearchOption.AllDirectories).Select(f => Path.GetRelativePath(merged, f)).Order());
        Assert.All(files, f => Assert.True(File.ReadAllBytes(Path.Combine(assembled, f)).SequenceEqual(File.ReadAllBytes(Path.Combine(merged, f))), f));
    }

    /// <summary>Spec v3 §4 layer 1: a Windows clock off NTP cannot be ready, and a round recorded with one fails the gate.</summary>
    [Fact]
    public async Task AClockOffNtpCannotBeReady()
    {
        using var skewed = NtpResponder.Bind(0);
        string skewedServer = $"127.0.0.1:{((IPEndPoint)skewed.Client.LocalEndPoint!).Port}";
        _ = Task.Run(async () =>
        {
            try
            {
                while (!_stop.IsCancellationRequested)
                {
                    var request = await skewed.ReceiveAsync(_stop.Token);
                    double now = NtpResponder.NowUnixMs() + 6507;  // an NTP server 6.5 s ahead of this machine
                    if (NtpResponder.Reply(request.Buffer, now, now) is { } reply) await skewed.SendAsync(reply, request.RemoteEndPoint, _stop.Token);
                }
            }
            catch (Exception e) when (e is OperationCanceledException or ObjectDisposedException or SocketException)
            {
            }
        });
        var (off, _) = Connect(null, "p1", _room, timeServer: skewedServer);
        var (good, _) = Connect(null, "p2", _room, timeServer: _ntpServer);
        await WaitForAgents(2);
        for (int i = 0; i < 200 && (off.Clock is null || good.Clock is null); i++) await Task.Delay(50);

        Assert.NotNull(off.Clock);
        Assert.InRange(off.Clock!.NtpOffsetMs!.Value, 6400, 6600);
        Assert.False(off.Clock.Ok);
        Assert.False(await off.SetReadyAsync(true, _stop.Token));
        Assert.False(off.IsReady);
        Assert.NotNull(good.Clock);
        Assert.True(good.Clock!.Ok, $"{good.Clock.NtpOffsetMs} ms, {good.Clock.NtpError}");
        Assert.NotNull(good.Clock.OrchestratorNtpOffsetMs);
        Assert.True(await good.SetReadyAsync(true, _stop.Token));
        Assert.True(good.IsReady);
    }

    [Fact]
    public async Task ABlackCaptureSourceCannotBeReadyUntilProbedAgain()
    {
        var black = new CaptureProbe(1920, 1080, 10, true, 0, "popup", "window:game");
        var (agent, _) = Connect(null, "p1", _room);
        agent.ProbeCapture = (_, _) => Task.FromResult<CaptureProbe?>(black);
        await WaitForAgents(1);
        for (int i = 0; i < 200 && agent.Capture is null; i++) await Task.Delay(50);

        Assert.Equal(black, agent.Capture);
        Assert.False(await agent.SetReadyAsync(true, _stop.Token));
        agent.ProbeCapture = (_, _) => Task.FromResult<CaptureProbe?>(black with { Black = false, LumaMean = 80, WindowStyle = "borderless" });
        Assert.True(await agent.SetReadyAsync(true, _stop.Token));  // probed again: the game is borderless now
        Assert.True(agent.Capture!.Ok);
    }

    [SkippableFact]
    public async Task ARoundWhoseClocksAreOffNtpFailsTheGate()
    {
        Skip.IfNot(SessionFixtures.HasFfmpeg, "needs ffmpeg + ffprobe");
        string template = SessionFixtures.MakeValidSession(Path.Combine(_root, "template"));
        var started = ConnectAgents(template, "p1", "p2");
        await WaitForAgents(2);
        var session = NextSession();
        await Post(new { type = "match_start", match_id = "m7" });
        await Post(new { type = "round_start" });
        await Task.WhenAll(started.Select(s => s.Task)).WaitAsync(TimeSpan.FromSeconds(10));
        await Post(new { type = "round_end" });
        var (dir, report) = await session;
        Assert.True(report.Pass, string.Join("; ", report.Issues.Concat(report.Players.SelectMany(p => p.Issues)).Select(i => i.Message)));
        var system = JsonNode.Parse(File.ReadAllText(Path.Combine(dir, "players", "p1", "player.json")))!["clock_sync"]!["system_clock"]!;
        Assert.InRange(system["round_offset_ms"]!.GetValue<double>(), -50, 50);

        // The same round with the orchestrator's clock 6.5 s off NTP: every player's clock is then off too.
        string staging = Path.Combine(_root, "test", report.SessionId);
        (RoundRecord.Load(staging) with { ServerClock = new ServerClock("ntp.test", 6507, 5, NtpResponder.NowUnixMs()) }).Save(staging);
        var (_, off) = MultiplayerSession.Assemble(staging, Path.Combine(_root, "out-off"), MergeCommand.Vendor, FixtureTolerances);
        Assert.False(off.Pass);
        Assert.Contains(off.Issues, i => i.Rule == "session.server_clock");
        Assert.All(off.Players, p => Assert.Contains(p.Issues, i => i.Rule == "sync.system_clock"));
    }

    private static void CopyDir(string src, string dst)
    {
        Directory.CreateDirectory(dst);
        foreach (string f in Directory.GetFiles(src)) File.Copy(f, Path.Combine(dst, Path.GetFileName(f)), true);
        foreach (string d in Directory.GetDirectories(src)) CopyDir(d, Path.Combine(dst, Path.GetFileName(d)));
    }

    [Fact]
    public async Task FailedCapturesFailTheSessionGate()
    {
        ConnectAgents(null, "p1", "p2");
        await WaitForAgents(2);
        var session = NextSession();

        await Post(new { type = "round_start", round = 3 });
        await Post(new { type = "round_end" });

        var (dir, report) = await session;
        Assert.False(report.Pass);
        Assert.Equal(0, report.ValidPlayers);
        Assert.Contains(report.Issues, i => i.Rule == "session.min_players");
        Assert.All(report.Players, p => Assert.Contains(p.Issues, i => i.Rule == "player.missing"));
        Assert.EndsWith("-r03", report.SessionId);
        Assert.True(File.Exists(Path.Combine(dir, "session.json")));
        Assert.True(File.Exists(Path.Combine(dir, "validation.json")));
    }

    [Fact]
    public async Task HostStartsRoundWithCountdownOnEveryPlayerAndEndsIt()
    {
        var (_, hostStarted, code) = await HostRoom("p1");
        var (member, memberStarted) = Connect(null, "p2", code);
        var started = new List<TaskCompletionSource> { hostStarted, memberStarted };
        await WaitForAgents(2, code);
        await member.SetReadyAsync(true, default);
        for (int i = 0; i < 100 && !_agents[0].Room!.Members.All(m => m.PlayerId == "p1" || m.Ready); i++) await Task.Delay(50);
        var countdowns = _agents.Select(a =>
        {
            var tcs = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
            a.Countdown += (ms, _) => tcs.TrySetResult(ms);
            return tcs.Task;
        }).ToList();
        var session = NextSession();

        await _agents[0].ToggleRoundAsync(300, default);
        int[] countdownMs = await Task.WhenAll(countdowns).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal([300, 300], countdownMs);
        await Task.WhenAll(started.Select(s => s.Task)).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.NotNull((await RoomStatus(code))["current_session"]);
        await Task.Delay(600);  // past the countdown: ending during it would cancel the round
        await _agents[0].ToggleRoundAsync(300, default);

        var (_, report) = await session;
        Assert.Equal(2, report.Players.Count);
        Assert.All(report.Players, p => Assert.Contains(p.Issues, i => i.Message == "no game window"));
    }

    [Fact]
    public async Task StoppingDuringTheCountdownCancelsTheRoundAndItsNumberIsUsedAgain()
    {
        var (_, hostStarted, code) = await HostRoom("p1");
        var started = new List<TaskCompletionSource> { hostStarted };
        await WaitForAgents(1, code);
        var cancelled = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        _coordinator.RoundCancelled += sid => cancelled.TrySetResult(sid);

        await _agents[0].ToggleRoundAsync(10_000, default);
        await started[0].Task.WaitAsync(TimeSpan.FromSeconds(5));
        await _agents[0].ToggleRoundAsync(10_000, default);
        string cancelledId = await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await WaitForAgents(1, code);
        var status = await RoomStatus(code);
        Assert.Null(status["current_session"]);
        Assert.Empty(status["pending_sessions"]!.AsArray());

        var session = NextSession();
        await _agents[0].ToggleRoundAsync(200, default);
        await Task.Delay(600);
        await _agents[0].ToggleRoundAsync(200, default);
        var (dir, report) = await session;
        Assert.Equal(cancelledId, report.SessionId);
        var json = JsonNode.Parse(File.ReadAllText(Path.Combine(dir, "session.json")))!;
        Assert.Equal("3.1", (string?)json["spec_version"]);
        Assert.Equal(200, (int)json["countdown_ms"]!);
        Assert.Equal("player_stop:p1", (string?)json["end_reason"]);
        var types = MultiplayerSession.ReadEvents(Path.Combine(dir, "server", "events.jsonl")).Select(GameEvents.Type).ToList();
        Assert.Equal(["round_cancelled", "round_countdown", "round_start", "round_end"], types);
    }

    [Fact]
    public async Task ReadyAloneNeverStartsARound()
    {
        var started = ConnectAgents(null, "p1", "p2");
        await WaitForAgents(2);

        await _agents[0].SetReadyAsync(true, default);
        await _agents[1].SetReadyAsync(true, default);
        await Task.Delay(1500);

        var status = await RoomStatus();
        Assert.Null(status["current_session"]);
        Assert.All(status["members"]!.AsArray(), m => Assert.True((bool)m!["ready"]!));
        Assert.DoesNotContain(started, s => s.Task.IsCompleted);
    }

    [Fact]
    public async Task UnreadyDuringTheHostsCountdownCancelsTheRound()
    {
        var (host, _, code) = await HostRoom("p1");
        var (member, _) = Connect(null, "p2", code);
        await WaitForAgents(2, code);
        var countdown = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        member.Countdown += (_, _) => countdown.TrySetResult();
        var cancelled = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        _coordinator.RoundCancelled += sid => cancelled.TrySetResult(sid);

        await member.SetReadyAsync(true, default);
        for (int i = 0; i < 100 && !host.Room!.Members.All(m => m.PlayerId == "p1" || m.Ready); i++) await Task.Delay(50);
        await host.ToggleRoundAsync(10_000, default);
        await countdown.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await member.SetReadyAsync(false, default);

        Assert.EndsWith("-r01", await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Null((await RoomStatus(code))["current_session"]);
    }

    [Fact]
    public async Task OnlyTheHostsRoomIsRecorded()
    {
        var (_, aStarted, a) = await HostRoom("p1");
        var (member, memberStarted) = Connect(null, "p2", a);
        var (otherHost, bStarted, b) = await HostRoom("p3");
        await WaitForAgents(2, a);
        Assert.NotEqual(a, b);
        await member.SetReadyAsync(true, default);
        for (int i = 0; i < 100 && !_agents[0].Room!.Members.All(m => m.PlayerId == "p1" || m.Ready); i++) await Task.Delay(50);

        await _agents[0].ToggleRoundAsync(300, default);
        await Task.WhenAll(aStarted.Task, memberStarted.Task).WaitAsync(TimeSpan.FromSeconds(5));
        await Task.Delay(500);
        Assert.False(bStarted.Task.IsCompleted);
        Assert.Null((await RoomStatus(b))["current_session"]);
        Assert.Equal(["p1", "p2"], (await RoomStatus(a))["members"]!.AsArray().Select(m => (string)m!["player_id"]!).Order());
        Assert.True(otherHost.IsHost);
        Assert.False(_agents[1].IsHost);
    }

    [Fact]
    public async Task TheHostAndAMemberRejoinTheirRoomAfterLosingTheConnection()
    {
        var connections = new Dictionary<string, CancellationTokenSource>();
        AgentClient Agent(string player, string? room)
        {
            var agent = new AgentClient(new AgentOptions(_http.BaseAddress!, player, player, Path.Combine(_root, "agent-" + player), null,
                RoomCode: room, CreateRoom: room is null), _ => throw new InvalidOperationException("no capture"));
            connections[player] = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
            _ = agent.RunAsync(connections[player].Token);
            return agent;
        }
        void Reconnect(AgentClient agent, string player)
        {
            connections[player].Cancel();
            connections[player] = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
            _ = agent.RunAsync(connections[player].Token);
        }

        var host = Agent("p1", null);
        for (int i = 0; i < 100 && host.Room is null; i++) await Task.Delay(50);
        string code = host.Room!.Code;
        var member = Agent("p2", code);
        await WaitForAgents(2, code);

        Reconnect(member, "p2");
        Reconnect(host, "p1");
        await Task.Delay(300);
        await WaitForAgents(2, code);
        var status = await RoomStatus(code);
        Assert.Equal("p1", (string?)status["host"]);
        Assert.Equal(["p1", "p2"], status["members"]!.AsArray().Select(m => (string)m!["player_id"]!).Order());
        for (int i = 0; i < 100 && host.Room?.Members.Count != 2; i++) await Task.Delay(50);
        Assert.True(host.IsHost);
        Assert.False(member.IsHost);
    }

    [Fact]
    public async Task AHostedRoomWaitsForItsHostAndTheHostForEveryoneReady()
    {
        var (host, hostStarted, code) = await HostRoom("p1");
        var (member, _) = Connect(null, "p2", code);
        await WaitForAgents(2, code);
        var refused = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        host.RoomError += m => refused.TrySetResult(m);

        await host.ToggleRoundAsync(300, default);
        Assert.Contains("P2", await refused.Task.WaitAsync(TimeSpan.FromSeconds(5)));

        await member.SetReadyAsync(true, default);
        await Task.Delay(1500);
        Assert.Null((await RoomStatus(code))["current_session"]);
        Assert.False(hostStarted.Task.IsCompleted);

        await host.ToggleRoundAsync(300, default);
        await hostStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task TheHostNamesTheRoundAndEveryPlayersFolderTakesThatName()
    {
        string template = SessionFixtures.MakeValidSession(Path.Combine(_root, "template"));
        var (host, _, code) = await HostRoom("p1");
        var (member, memberStarted) = Connect(template, "p2", code);
        await WaitForAgents(2, code);
        var refused = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        host.RoomError += m => refused.TrySetResult(m);
        var kept = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        member.RoundKept += (sid, result) => kept.TrySetResult(result.SessionDir);

        await member.SetReadyAsync(true, default);
        for (int i = 0; i < 100 && !host.Room!.Members.All(m => m.PlayerId == "p1" || m.Ready); i++) await Task.Delay(50);
        await host.ToggleRoundAsync(0, default, "task 42");
        Assert.Contains("task id 'task 42'", await refused.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        await host.ToggleRoundAsync(0, default, "task-42");
        await memberStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("task-42", (string?)(await RoomStatus(code))["current_session"]);
        await host.ToggleRoundAsync(0, default);
        Assert.Equal("task-42", Path.GetFileName(await kept.Task.WaitAsync(TimeSpan.FromSeconds(10))));

        refused = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        for (int i = 0; i < 100 && host.Room!.Members.Any(m => m.State != "idle"); i++) await Task.Delay(50);
        await member.SetReadyAsync(true, default);
        for (int i = 0; i < 100 && !host.Room!.Members.All(m => m.PlayerId == "p1" || m.Ready); i++) await Task.Delay(50);
        await host.ToggleRoundAsync(0, default, "task-42");
        Assert.Contains("already used", await refused.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Null((await RoomStatus(code))["current_session"]);
    }

    [Fact]
    public async Task AHostCannotStartARoundWhileSomeoneStillSavesTheLastOne()
    {
        string template = SessionFixtures.MakeValidSession(Path.Combine(_root, "template"));
        var (host, _, code) = await HostRoom("p1");
        var (member, memberStarted) = Connect(template, "p2", code, building: TimeSpan.FromSeconds(3));
        await WaitForAgents(2, code);
        var refused = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        host.RoomError += m => refused.TrySetResult(m);
        var kept = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        member.RoundKept += (sid, _) => kept.TrySetResult(sid);

        await member.SetReadyAsync(true, default);
        for (int i = 0; i < 100 && !host.Room!.Members.All(m => m.PlayerId == "p1" || m.Ready); i++) await Task.Delay(50);
        await host.ToggleRoundAsync(0, default);
        await memberStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await host.ToggleRoundAsync(0, default);  // ends it: the member's capture now takes 3 s to build
        for (int i = 0; i < 100 && (await RoomStatus(code))["current_session"] is not null; i++) await Task.Delay(50);

        await member.SetReadyAsync(true, default);
        await host.ToggleRoundAsync(0, default);
        Assert.Contains("still saving the last round: P2", await refused.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Null((await RoomStatus(code))["current_session"]);

        Assert.EndsWith("-r01", await kept.Task.WaitAsync(TimeSpan.FromSeconds(10)));
        for (int i = 0; i < 100 && host.Room!.Members.Any(m => m.State != "idle"); i++) await Task.Delay(50);
        await member.SetReadyAsync(true, default);
        for (int i = 0; i < 100 && !host.Room!.Members.All(m => m.PlayerId == "p1" || m.Ready); i++) await Task.Delay(50);
        await host.ToggleRoundAsync(0, default);
        for (int i = 0; i < 100 && (await RoomStatus(code))["current_session"] is null; i++) await Task.Delay(50);
        Assert.EndsWith("-r02", (string?)(await RoomStatus(code))["current_session"]);
    }

    [Fact]
    public async Task UnknownInviteCodesAreRefusedAndTheHostClosesTheRoom()
    {
        var (stranger, _) = Connect(null, "p9", "ZZZZZZ");
        var refused = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        stranger.RoomError += m => refused.TrySetResult(m);
        Assert.Contains("ZZZZZZ", await refused.Task.WaitAsync(TimeSpan.FromSeconds(5)));

        var (host, _, code) = await HostRoom("p1");
        var (member, _) = Connect(null, "p2", code.ToLowerInvariant());
        await WaitForAgents(2, code);
        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        member.RoomChanged += r => { if (r is null) closed.TrySetResult(); };
        await host.CloseRoomAsync(default);
        await closed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Null(member.Room);
        Assert.Equal(System.Net.HttpStatusCode.NotFound, (await _http.GetAsync($"/rooms/{code}")).StatusCode);
    }
}

public sealed class OrchestratorAccessKeyTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "odx-key-" + Guid.NewGuid().ToString("N"));
    private WebApplication _app = null!;
    private Uri _url = null!;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_root);
        string profile = Path.Combine(_root, "game.json");
        File.WriteAllText(profile, """
            {"game_title": "Synthetic", "platform": "PC", "localization": "en-US",
             "input_mouse_convention": {"maps_to": "camera_look_velocity"}, "bindings": {}}
            """);
        _app = OrchestratorHost.Build(new OrchestratorOptions(profile, HttpPort: 0, TimePort: 0)
            { AccessKey = "s3cret" }, "127.0.0.1");
        await _app.StartAsync();
        _url = new Uri(_app.Urls.First());
    }

    public async Task DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
    }

    private AgentClient Agent(string? key) =>
        new(new AgentOptions(_url, "p1", "P1", Path.Combine(_root, "agent"), null, CreateRoom: true, AccessKey: key),
            _ => throw new InvalidOperationException("no capture in this test"));

    [Fact]
    public async Task Requests_without_the_key_are_refused()
    {
        using var anonymous = new HttpClient { BaseAddress = _url };
        Assert.Equal(System.Net.HttpStatusCode.Unauthorized, (await anonymous.PostAsync("/rooms", null)).StatusCode);
        Assert.Equal(System.Net.HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/status")).StatusCode);
        using var wrong = new HttpClient { BaseAddress = _url, DefaultRequestHeaders = { Authorization = new("Bearer", "guess") } };
        Assert.Equal(System.Net.HttpStatusCode.Unauthorized, (await wrong.GetAsync("/status")).StatusCode);

        var agent = Agent("guess");
        var refused = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        agent.RoomError += m => refused.TrySetResult(m);
        var run = agent.RunUntilStoppedAsync(default);
        Assert.Contains("access key", await refused.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        await run.WaitAsync(TimeSpan.FromSeconds(5));  // gives up instead of retrying
    }

    [Fact]
    public async Task The_key_opens_rooms()
    {
        using var keyed = new HttpClient { BaseAddress = _url, DefaultRequestHeaders = { Authorization = new("Bearer", "s3cret") } };
        Assert.True((await keyed.PostAsync("/rooms", null)).IsSuccessStatusCode);

        var agent = Agent("s3cret");
        using var stop = new CancellationTokenSource();
        _ = agent.RunUntilStoppedAsync(stop.Token);
        for (int i = 0; i < 100 && agent.Room is null; i++) await Task.Delay(50);
        Assert.True(agent.IsHost);
        stop.Cancel();
    }
}

/// <summary>Against this machine's ZeroTier service, which is also a controller; skipped where ZeroTier is not installed.</summary>
/// <summary>The machine's one ZeroTier service is slow to serve two tests creating networks at once (requests time out).</summary>
[Collection("ZeroTier")]
public sealed class RoomLanTests
{
    [SkippableFact]
    public async Task A_lan_room_gets_its_own_network_that_members_join_and_closing_deletes()
    {
        string? token = ZeroTierApi.LocalToken();
        Skip.If(token is null, "ZeroTier is not installed here");
        string root = Path.Combine(Path.GetTempPath(), "odx-lan-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string profile = Path.Combine(root, "game.json");
        File.WriteAllText(profile, """
            {"game_title": "Synthetic", "platform": "PC", "localization": "en-US",
             "input_mouse_convention": {"maps_to": "camera_look_velocity"}, "bindings": {}}
            """);
        await using var app = OrchestratorHost.Build(new OrchestratorOptions(profile, HttpPort: 0, TimePort: 0)
            { ZeroTierApi = ZeroTierApi.Local, ZeroTierToken = token }, "127.0.0.1");
        await app.StartAsync();
        using var controller = new ZeroTierApi(ZeroTierApi.Local, token!);

        var host = new AgentClient(new AgentOptions(new Uri(app.Urls.First()), "p1", "P1", Path.Combine(root, "agent"), null,
            CreateRoom: true, Lan: true), _ => throw new InvalidOperationException("no capture in this test"));
        var lan = new TaskCompletionSource<LanState>(TaskCreationOptions.RunContinuationsAsynchronously);
        host.LanChanged += s => { if (s is { Status: not "joining" }) lan.TrySetResult(s); };
        using var stop = new CancellationTokenSource();
        _ = host.RunUntilStoppedAsync(stop.Token);

        var state = await lan.Task.WaitAsync(TimeSpan.FromSeconds(60));
        Assert.True(state.Status == "ok", $"{state.Status}: {state.Address}");
        Assert.StartsWith("10.147.20.", state.Address);
        Assert.Equal(host.Room!.LanNetworkId, state.NetworkId);

        await host.CloseRoomAsync(default);
        await Task.Delay(1000);
        Assert.Equal("NOT_FOUND", (await controller.NetworkAsync(state.NetworkId, default)).Status);
        using var http = new HttpClient { DefaultRequestHeaders = { { "X-ZT1-Auth", token } } };
        Assert.DoesNotContain(state.NetworkId, await http.GetStringAsync(new Uri(ZeroTierApi.Local, "/controller/network")));
        stop.Cancel();
        await app.StopAsync();
    }

    [Fact]
    public async Task Without_a_controller_a_lan_room_is_refused()
    {
        string root = Path.Combine(Path.GetTempPath(), "odx-nolan-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string profile = Path.Combine(root, "game.json");
        File.WriteAllText(profile, """
            {"game_title": "Synthetic", "platform": "PC", "localization": "en-US",
             "input_mouse_convention": {"maps_to": "camera_look_velocity"}, "bindings": {}}
            """);
        await using var app = OrchestratorHost.Build(new OrchestratorOptions(profile, HttpPort: 0, TimePort: 0),
            "127.0.0.1");
        await app.StartAsync();
        var host = new AgentClient(new AgentOptions(new Uri(app.Urls.First()), "p1", "P1", Path.Combine(root, "agent"), null,
            CreateRoom: true, Lan: true), _ => throw new InvalidOperationException("no capture in this test"));
        var refused = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        host.RoomError += m => refused.TrySetResult(m);
        using var stop = new CancellationTokenSource();
        _ = host.RunUntilStoppedAsync(stop.Token);
        Assert.Contains("LAN", await refused.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Null(host.Room);
        stop.Cancel();
        await app.StopAsync();
    }
}

public sealed class PacketCaptureTests
{
    private sealed class FakePackets : IPacketCapture
    {
        public List<string> Calls { get; } = [];

        public Task StartAsync(IReadOnlyList<string> ports, string etlPath, CancellationToken stop)
        {
            Calls.Add($"start {string.Join(',', ports)}");
            return Task.CompletedTask;
        }

        public Task StopAsync(string etlPath, string pcapngPath, CancellationToken stop)
        {
            Calls.Add($"stop {Path.GetFileName(pcapngPath)}");
            File.WriteAllBytes(pcapngPath, [0x0A, 0x0D, 0x0D, 0x0A]);
            return Task.CompletedTask;
        }
    }

    [Fact]
    public void Profiles_declare_the_traffic_to_capture()
    {
        string games = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "..", "profiles"));
        Assert.Equal("w3gs", GameProfile.Load(Path.Combine(games, "warcraft3.json")).Network!.Protocol);
        Assert.Equal(["tcp/6112", "udp/6112"], GameProfile.Load(Path.Combine(games, "warcraft3.json")).Network!.Ports);
        Assert.Equal("raw", GameProfile.Load(Path.Combine(games, "cs2.json")).Network!.Protocol);
        Assert.Null(GameProfile.Load(Path.Combine(games, "example-fps.json")).Network);
        Assert.Throws<FormatException>(() => PacketCaptureSpec.Parse(new System.Text.Json.Nodes.JsonObject
            { ["ports"] = new System.Text.Json.Nodes.JsonArray("6112; calc.exe") }, "test"));
    }

    [Fact]
    public async Task A_round_starts_and_stops_the_capture_and_a_failed_capture_discards_it()
    {
        string root = Path.Combine(Path.GetTempPath(), "odx-pkt-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string profile = Path.Combine(root, "game.json");
        File.WriteAllText(profile, """
            {"game_title": "Synthetic", "platform": "PC", "localization": "en-US",
             "network": {"protocol": "w3gs", "ports": ["tcp/6112"]},
             "input_mouse_convention": {"maps_to": "camera_look_velocity"}, "bindings": {}}
            """);
        await using var app = OrchestratorHost.Build(new OrchestratorOptions(profile, HttpPort: 0, TimePort: 0),
            "127.0.0.1");
        await app.StartAsync();
        var packets = new FakePackets();
        var host = new AgentClient(new AgentOptions(new Uri(app.Urls.First()), "p1", "P1", Path.Combine(root, "agent"), null,
            CreateRoom: true), _ => throw new InvalidOperationException("no game window"));
        var wanted = new TaskCompletionSource<PacketCaptureSpec>(TaskCreationOptions.RunContinuationsAsynchronously);
        host.PacketCaptureWanted += spec =>
        {
            host.Packets = packets;
            wanted.TrySetResult(spec);
        };
        using var stop = new CancellationTokenSource();
        _ = host.RunUntilStoppedAsync(stop.Token);
        Assert.Equal("w3gs", (await wanted.Task.WaitAsync(TimeSpan.FromSeconds(5))).Protocol);
        for (int i = 0; i < 100 && host.Room is null; i++) await Task.Delay(50);

        await host.ToggleRoundAsync(0, default);
        for (int i = 0; i < 100 && packets.Calls.Count < 2; i++) await Task.Delay(50);
        Assert.Equal("start tcp/6112", packets.Calls[0]);
        Assert.StartsWith("stop odx-packets-p1-", packets.Calls[1]);  // the capture failed, so its packets are discarded
        stop.Cancel();
        await app.StopAsync();
    }
}

public sealed class ProtocolVersionTests
{
    [Fact]
    public async Task An_agent_speaking_another_protocol_is_told_so_and_stops()
    {
        string root = Path.Combine(Path.GetTempPath(), "odx-proto-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string profile = Path.Combine(root, "game.json");
        File.WriteAllText(profile, """
            {"game_title": "Synthetic", "platform": "PC", "localization": "en-US",
             "input_mouse_convention": {"maps_to": "camera_look_velocity"}, "bindings": {}}
            """);
        await using var app = OrchestratorHost.Build(new OrchestratorOptions(profile, HttpPort: 0, TimePort: 0), "127.0.0.1");
        await app.StartAsync();
        var http = new Uri(app.Urls.First());

        using var old = new System.Net.WebSockets.ClientWebSocket();
        await old.ConnectAsync(new UriBuilder(http) { Scheme = "ws", Path = "/agents" }.Uri, default);
        await old.SendAsync(System.Text.Encoding.UTF8.GetBytes("""{"type":"hello","player_id":"old","name":"Old","tool_version":"0.0.9"}"""),
            System.Net.WebSockets.WebSocketMessageType.Text, true, default);
        var answer = AgentMessage.Parse((await WebSocketText.ReceiveAsync(old, new byte[4096], default))!);
        Assert.Equal(AgentMessage.Version, Assert.IsType<Incompatible>(answer).Protocol);

        var agent = new AgentClient(new AgentOptions(http, "p1", "P1", Path.Combine(root, "p1"), null, CreateRoom: true),
            _ => throw new InvalidOperationException("no capture in this test"));
        using var stop = new CancellationTokenSource();
        _ = agent.RunUntilStoppedAsync(stop.Token);
        for (int i = 0; i < 100 && agent.Room is null; i++) await Task.Delay(50);
        Assert.NotNull(agent.Room);  // the same release gets in
        stop.Cancel();
        await app.StopAsync();
    }
}
