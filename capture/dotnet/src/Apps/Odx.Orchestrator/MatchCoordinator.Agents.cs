using System.Net.WebSockets;
using System.Text;
using Odx.Core;
using Odx.Protocol;

namespace Odx.Orchestrator;

public sealed partial class MatchCoordinator
{
    public async Task HandleAgentAsync(WebSocket socket, CancellationToken stop)
    {
        var buffer = new byte[64 * 1024];
        if (await ReceiveAsync(socket, buffer, stop) is not Hello hello)
        {
            await socket.CloseAsync(WebSocketCloseStatus.PolicyViolation, "expected hello", stop);
            return;
        }
        if (hello.Protocol != AgentMessage.Version)
        {
            log.LogWarning("agent {Agent} ({Version}) speaks protocol {Protocol}, not {Ours}: refused", hello.PlayerId,
                hello.ToolVersion, hello.Protocol, AgentMessage.Version);
            await socket.SendAsync(Encoding.UTF8.GetBytes(new Incompatible(AgentMessage.Version, BuildInfo.Version).ToJson()),
                WebSocketMessageType.Text, true, stop);
            await socket.CloseAsync(WebSocketCloseStatus.PolicyViolation, "incompatible protocol", stop);
            return;
        }
        var link = new AgentLink(hello, socket);
        if (hello.Role == Hello.GameServerRole)
        {
            await HandleGameServerAsync(link, buffer, stop);
            return;
        }
        var resumed = new Effects();
        AgentLink? elsewhere = null;
        lock (_lock)
        {
            // Two machines on one player id get each other's Start and Stop: the second is turned away (2026-11-08, "alan").
            if (_agents.GetValueOrDefault(hello.PlayerId) is { } other && other.Socket.State == WebSocketState.Open &&
                other.Hello.MachineId != hello.MachineId)
                elsewhere = other;
            else
            {
                _agents[hello.PlayerId] = link;
                Resume(link, resumed);
            }
        }
        if (elsewhere is not null)
        {
            log.LogWarning("agent {Player} ({Name}) refused: the id is in use on another machine ({Other})", hello.PlayerId,
                hello.Name, elsewhere.Hello.Name);
            await socket.SendAsync(Encoding.UTF8.GetBytes(new RoomError(
                $"player id '{hello.PlayerId}' is in use on another machine ({elsewhere.Hello.Name}); set a player id of your own").ToJson()),
                WebSocketMessageType.Text, true, stop);
            await socket.CloseAsync(WebSocketCloseStatus.PolicyViolation, "player id in use", stop);
            return;
        }
        log.LogInformation("agent {Player} ({Name}, {Version}) connected", hello.PlayerId, hello.Name, hello.ToolVersion);
        await SendAsync(link, new Welcome(timePort, (int)options.ReconnectGrace.TotalMilliseconds, Volatile.Read(ref _serverClock)));
        await RunAsync(resumed);

        try
        {
            while (!stop.IsCancellationRequested && await ReceiveAsync(socket, buffer, stop) is { } message)
            {
                if (message is Done d)
                {
                    if (!Knows(d.SessionId)) await SendFinishedRoundAsync(link, d.SessionId);
                    else MarkDone(d.SessionId, hello.PlayerId, d.Ok, d.Error, d.Report);
                    continue;
                }
                if (message is LanMember lan)
                {
                    _ = AuthorizeLanAsync(link, lan.NodeId, stop);  // an HTTP call: off this loop, which must keep reading PONGs
                    continue;
                }
                if (message is CreateRoom { Game: { } wanted } && !_profiles.ContainsKey(wanted))
                {
                    await SendAsync(link, new RoomError(
                        $"this orchestrator has no game profile \"{wanted}\"; it has: {string.Join(", ", _profiles.Keys.Order())}"));
                    continue;
                }
                string? lanNetwork = null;
                if (message is CreateRoom { Lan: true })
                {
                    lanNetwork = await CreateLanAsync(link, stop);
                    if (lanNetwork is null) continue;
                }
                var effects = new Effects();
                lock (_lock) Handle(link, message, effects, lanNetwork);
                await RunAsync(effects);
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            var effects = new Effects();
            long? absence = null;
            lock (_lock)
            {
                if (_agents.GetValueOrDefault(hello.PlayerId) == link)
                {
                    _agents.Remove(hello.PlayerId);
                    _absent[hello.PlayerId] = (long)(absence = ++_absences);
                }
                Leave(link, absence is null ? "reconnected" : "disconnected", effects);
            }
            log.LogWarning("agent {Player} disconnected", hello.PlayerId);
            await RunAsync(effects);
            if (absence is { } a)
                _ = Task.Delay(options.ReconnectGrace).ContinueWith(_ => PlayerGoneAsync(hello.PlayerId, a), TaskScheduler.Default).Unwrap();
        }
    }

    /// <summary>
    /// After a lost connection, with the lock held: the rounds this agent still records go on (it is no longer away) or get
    /// the Stop it missed; a round the orchestrator no longer knows is stopped too.
    /// </summary>
    private void Resume(AgentLink link, Effects effects)
    {
        _absent.Remove(link.Id);
        _awayLan.Remove(link.Id);
        foreach (string sid in link.Hello.Recording ?? [])
        {
            link.State = "recording";
            var round = _rounds.GetValueOrDefault(sid);
            bool server = round?.Server == link.Id;
            int player = round?.IndexOf(link.Id) ?? -1;
            if (round is null || !server && player < 0)
            {
                effects.Sends.Add((link, new Stop(sid, "disconnected")));
                continue;
            }
            if (round.Away.Remove(link.Id)) log.LogInformation("{Agent} back in {Session}", link.Id, sid);
            string? missed = server
                ? round.Ended ? round.Record.EndReason ?? GameEvents.RoundEnd : null
                : round.Record.Players[player].StopReason;
            if (missed is not null) effects.Sends.Add((link, new Stop(sid, missed)));
        }
    }

    /// <summary>A player still gone <see cref="OrchestratorOptions.ReconnectGrace"/> after losing the connection.</summary>
    private async Task PlayerGoneAsync(string playerId, long absence)
    {
        var effects = new Effects();
        lock (_lock)
        {
            if (_absent.GetValueOrDefault(playerId) != absence) return;
            _absent.Remove(playerId);
            if (_awayLan.Remove(playerId, out var lan)) effects.LanRevoke.Add(lan);
            double utc = NtpResponder.NowUnixMs();
            foreach (var round in _rounds.Values.Where(r => r.Away.Remove(playerId)))
            {
                if (!round.Ended) StopPlayer(round, playerId, utc, "disconnected", effects);
                effects.Changed.Add(round.Room);
            }
        }
        await RunAsync(effects);
        foreach (string sid in RoundsWaitingFor(playerId)) MarkDone(sid, playerId, false, "agent disconnected", null);
    }

    /// <summary>The game server agent: told to start and stop with every round from now on, it answers with Done.</summary>
    private async Task HandleGameServerAsync(AgentLink link, byte[] buffer, CancellationToken stop)
    {
        var resumed = new Effects();
        lock (_lock)
        {
            _gameServers[link.Id] = link;
            Resume(link, resumed);
        }
        log.LogInformation("game server agent {Id} ({Name}, {Version}) connected", link.Id, link.Hello.Name, link.Hello.ToolVersion);
        await SendAsync(link, new Welcome(timePort, (int)options.ReconnectGrace.TotalMilliseconds, Volatile.Read(ref _serverClock)));
        await RunAsync(resumed);
        try
        {
            while (!stop.IsCancellationRequested && await ReceiveAsync(link.Socket, buffer, stop) is { } message)
            {
                if (message is not Done d) continue;
                if (!Knows(d.SessionId)) await SendFinishedRoundAsync(link, d.SessionId);
                else MarkServerDone(link.Id, d.SessionId, d.Ok, d.Error);
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            long? absence = null;
            lock (_lock)
                if (_gameServers.GetValueOrDefault(link.Id) == link)
                {
                    _gameServers.Remove(link.Id);
                    _absent[link.Id] = (long)(absence = ++_absences);
                }
            log.LogWarning("game server agent {Id} disconnected", link.Id);
            if (absence is { } a)
                _ = Task.Delay(options.ReconnectGrace).ContinueWith(_ => GameServerGone(link.Id, a), TaskScheduler.Default);
        }
    }

    /// <summary>A game server agent still gone <see cref="OrchestratorOptions.ReconnectGrace"/> after losing the connection.</summary>
    private void GameServerGone(string serverId, long absence)
    {
        List<string> waiting;
        lock (_lock)
        {
            if (_absent.GetValueOrDefault(serverId) != absence) return;
            _absent.Remove(serverId);
            waiting = _rounds.Values.Where(r => r.Server == serverId && !r.ServerDone).Select(r => r.Record.SessionId).ToList();
        }
        foreach (string sid in waiting) MarkServerDone(serverId, sid, false, "game server agent disconnected");
    }

    private bool Knows(string sessionId)
    {
        lock (_lock) return _rounds.ContainsKey(sessionId);
    }

    /// <summary>
    /// A Done for a round that finished while its sender was away (it is sent again on reconnect): the round's files, as
    /// the others got them, so that folder is complete too.
    /// </summary>
    private async Task SendFinishedRoundAsync(AgentLink link, string sessionId)
    {
        StoredRound? round;
        lock (_lock) round = Store?.Round(sessionId);
        if (round is not { State: "finished" }) return;
        log.LogInformation("{Agent}: the files of {Session}, which finished while it was away", link.Id, sessionId);
        await SendAsync(link, new RoundFiles(sessionId, round.Record.ToJson(), RoundRecord.EventsJsonl(round.Events), ProfileJson(round.Record.Game)));
    }

    private void MarkDone(string sessionId, string playerId, bool ok, string? error, CaptureReport? report)
    {
        OpenRound? round;
        string verdict = report?.Verdict ?? (ok ? CaptureReport.Ok : CaptureReport.Failed);
        var problems = report?.Problems() ?? (error is null ? [] : [error]);
        lock (_lock)
        {
            if (!_rounds.TryGetValue(sessionId, out round) || round.IndexOf(playerId) is var i && i < 0) return;
            round.Record.Players[i] = round.Record.Players[i] with
            {
                Done = true, Kept = ok, Error = error, Report = report, Verdict = verdict, Problems = problems,
            };
            Save(round);
            if (verdict == CaptureReport.Ok) log.LogInformation("{Player} done with {Session}: ok", playerId, sessionId);
            else log.LogWarning("{Player} done with {Session}: {Verdict}: {Problems}", playerId, sessionId, verdict, string.Join("; ", problems));
            if (!round.Ended || !AllDone(round)) return;
        }
        _ = FinishAsync(round);
    }

    private void MarkServerDone(string serverId, string sessionId, bool ok, string? error)
    {
        OpenRound? round;
        lock (_lock)
        {
            if (!_rounds.TryGetValue(sessionId, out round) || round.Server != serverId || round.ServerDone) return;
            round.ServerDone = true;
            Save(round);
            if (!ok) log.LogWarning("game server agent {Id}, {Session}: {Error}", serverId, sessionId, error);
            if (!round.Ended || !AllDone(round)) return;
        }
        _ = FinishAsync(round);
    }

    /// <summary>Called with the lock held.</summary>
    private static bool AllDone(OpenRound round) => round.Record.Players.All(p => p.Done) && (round.Server is null || round.ServerDone);

    private List<string> RoundsWaitingFor(string playerId)
    {
        lock (_lock)
            return _rounds.Values.Where(r => r.IndexOf(playerId) is var i && i >= 0 && !r.Record.Players[i].Done)
                .Select(r => r.Record.SessionId).ToList();
    }
}
