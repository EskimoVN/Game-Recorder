using System.Globalization;
using System.Text.Json.Nodes;
using Odx.Core;
using Odx.Protocol;

namespace Odx.Orchestrator;

public sealed partial class MatchCoordinator
{
    /// <summary>Called with the lock held.</summary>
    private void Apply(Room room, JsonObject e, Effects effects)
    {
        e["utc_ms"] ??= NtpResponder.NowUnixMs();
        double utc = GameEvents.UtcMs(e);
        string type = GameEvents.Type(e);
        Stamp(room, e, utc, effects);
        effects.Changed.Add(room);

        if (room.Current is { Counting: true } counting &&
            type is GameEvents.RoundStart or GameEvents.RoundEnd or GameEvents.MatchStart or GameEvents.MatchEnd)
        {
            Cancel(counting, EndReason(e), effects);
            if (type == GameEvents.RoundEnd) return;  // the cancel is the whole story of this round
        }
        switch (type)
        {
            case GameEvents.MatchStart:
                if (room.Current is not null) EndRound(room, utc, "abort", "abort", effects);
                StartMatch(room, e);
                break;
            case GameEvents.RoundStart:
                if (room.Current is not null) EndRound(room, utc, "abort", "abort", effects);
                if (room.MatchId is null) StartMatch(room, e);
                StartRound(room, e, utc, effects);
                break;
            case GameEvents.PlayerDeath when room.Current is not null && e["permanent"]?.GetValue<bool>() == true &&
                                             GameEvents.Text(e, "player") is { } player:
                StopPlayer(room.Current, player, utc, "permanent_death", effects);
                break;
        }
        AddToRound(room, e);
        if (type is GameEvents.RoundEnd or GameEvents.MatchEnd && room.Current is not null)
            EndRound(room, utc, "round_end", EndReason(e), effects);
        if (type == GameEvents.MatchEnd) room.MatchId = null;
    }

    private void Stamp(Room room, JsonObject e, double utc, Effects effects)
    {
        e["event_id"] = ++_eventId;
        e["utc"] = UtcText.IsoUs(utc);
        e["server_time_us"] = (long)Math.Round(utc * 1000);
        foreach (var member in room.Members) effects.Sends.Add((member, new GameEvent(_eventId)));
    }

    private static void AddToRound(Room room, JsonObject e)
    {
        if (room.Current is not { } round)
        {
            room.BetweenRounds.Add(e);
            return;
        }
        e["session_id"] = round.Record.SessionId;
        e["round_index"] = round.Record.Round;
        round.Events.Add(e);
    }

    private void StartMatch(Room room, JsonObject e)
    {
        room.MatchId = GameEvents.Text(e, "match_id") ??
                       $"{room.Code}-{DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture)}";
        room.Game = GameEvents.Text(e, "game") ?? _profileGame;
        room.Map = GameEvents.Text(e, "map");
        room.RoundNumber = 0;
        log.LogInformation("room {Room}: match {Match}, {Game} {Map}", room.Code, room.MatchId, room.Game, room.Map);
    }

    /// <summary>
    /// With a countdown, <paramref name="e"/> becomes round_countdown and round_start follows when play starts. The session
    /// is named by the host (<see cref="ToggleRound.SessionId"/>, already checked), else <c>&lt;match&gt;-rNN</c>.
    /// </summary>
    private void StartRound(Room room, JsonObject e, double utc, Effects effects)
    {
        room.RoundNumber = e["round"]?.GetValue<int>() ?? room.RoundNumber + 1;
        string sid = GameEvents.Text(e, "session_id") ?? $"{room.MatchId}-r{room.RoundNumber:D2}";
        var players = room.Members.Where(a => a.State is "idle" or "armed").ToList();
        foreach (var busy in room.Members.Except(players))
            log.LogWarning("{Player} is {State}, left out of {Session}", busy.Id, busy.State, sid);
        int countdownMs = e["countdown_ms"]?.GetValue<int>() ?? 0;
        var record = new RoundRecord(sid, room.MatchId!, room.Game, room.Map, room.RoundNumber, utc + countdownMs, null, timePort,
            players.Select(a => new RoundPlayer(a.Id, a.Hello.Name, ToolVersion: a.Hello.ToolVersion,
                MachineId: a.Hello.MachineId, SteamId: a.Hello.SteamId)).ToList(), countdownMs,
            ServerClock: Volatile.Read(ref _serverClock));
        var server = ServerFor(room);
        if (server is null && (room.Server is not null || _gameServers.Count > 1))
            log.LogWarning("{Session}: no game server records it (room bound to {Server}; connected: {Connected})", sid,
                room.Server ?? "none", string.Join(", ", _gameServers.Keys));
        var round = new OpenRound(room, record) { Counting = countdownMs > 0, Server = server?.Id };
        round.Events.AddRange(room.BetweenRounds);
        room.BetweenRounds.Clear();
        room.Current = round;
        _rounds[sid] = round;
        if (countdownMs == 0) ClearReady(room);
        foreach (var a in players)
        {
            a.State = "starting";
            effects.Sends.Add((a, new Arm(sid, _profileJson, options.Fps)));
            effects.Sends.Add((a, new Start(sid, countdownMs)));
        }
        if (server is not null) effects.Sends.Add((server, new Start(sid, countdownMs)));
        if (countdownMs > 0)
        {
            e["type"] = "round_countdown";
            e["starts_in_ms"] = countdownMs;
            effects.Countdown = round;
        }
        log.LogInformation("round {Session}: {Count} player(s){Countdown}", sid, players.Count,
            countdownMs > 0 ? $", play starts in {countdownMs / 1000.0:F0} s" : "");
    }

    private async Task CountdownEndedAsync(OpenRound round)
    {
        var effects = new Effects();
        lock (_lock)
        {
            if (round.Room.Current != round || !round.Counting) return;
            round.Counting = false;
            ClearReady(round.Room);
            var e = new JsonObject
            {
                ["type"] = GameEvents.RoundStart,
                ["utc_ms"] = round.Record.StartUtcMs,
                ["players"] = new JsonArray(round.Record.Players.Select(p => (JsonNode)p.PlayerId).ToArray()),
            };
            Stamp(round.Room, e, round.Record.StartUtcMs, effects);
            AddToRound(round.Room, e);
            effects.Changed.Add(round.Room);
        }
        log.LogInformation("round {Session}: play started", round.Record.SessionId);
        await RunAsync(effects);
    }

    /// <summary>Ready is for the next round once play starts; until then an unready cancels the countdown.</summary>
    private static void ClearReady(Room room)
    {
        foreach (var a in room.Members) a.Ready = false;
    }

    /// <summary>Drops a round during its countdown: everyone stops, nothing goes to a merge, its number is used again.</summary>
    private void Cancel(OpenRound round, string reason, Effects effects)
    {
        var room = round.Room;
        double utc = NtpResponder.NowUnixMs();
        round.Counting = false;
        round.Ended = true;
        room.Current = null;
        _rounds.Remove(round.Record.SessionId);
        room.RoundNumber = round.Record.Round - 1;
        foreach (var p in round.Record.Players.ToList()) StopPlayer(round, p.PlayerId, utc, Stop.Cancelled, effects);
        StopServer(round, Stop.Cancelled, effects);
        var e = new JsonObject { ["type"] = "round_cancelled", ["utc_ms"] = utc, ["cancelled_session_id"] = round.Record.SessionId,
            ["reason"] = reason };
        Stamp(room, e, utc, effects);
        room.BetweenRounds.Add(e);
        effects.Cancelled.Add(round);
        effects.Changed.Add(room);
        log.LogInformation("round {Session} cancelled: {Reason}", round.Record.SessionId, reason);
    }

    private void StopPlayer(OpenRound round, string playerId, double utc, string reason, Effects effects)
    {
        int i = round.IndexOf(playerId);
        if (i < 0 || round.Record.Players[i].StopReason is not null) return;
        round.Record.Players[i] = round.Record.Players[i] with { StopReason = reason, StopUtcMs = utc };
        if (_agents.TryGetValue(playerId, out var link)) effects.Sends.Add((link, new Stop(round.Record.SessionId, reason)));
        log.LogInformation("stop {Player} in {Session}: {Reason}", playerId, round.Record.SessionId, reason);
    }

    private void StopServer(OpenRound round, string reason, Effects effects)
    {
        if (round.Server is { } id && _gameServers.TryGetValue(id, out var server))
            effects.Sends.Add((server, new Stop(round.Record.SessionId, reason)));
    }

    /// <param name="playerReason">Stop reason of every player still recording.</param>
    /// <param name="endReason">Why the round ended, for session.json.</param>
    private void EndRound(Room room, double utc, string playerReason, string endReason, Effects effects)
    {
        var round = room.Current!;
        room.Current = null;
        round.Ended = true;
        round.Record = round.Record with { EndUtcMs = utc, EndReason = endReason };
        foreach (var p in round.Record.Players.ToList()) StopPlayer(round, p.PlayerId, utc, playerReason, effects);
        StopServer(round, playerReason, effects);
        effects.Ended.Add(round);
        effects.Changed.Add(room);
    }

    /// <summary>The game's own "reason", else "player_stop:&lt;id&gt;" when a host ended it, else the event type.</summary>
    private static string EndReason(JsonObject e) =>
        GameEvents.Text(e, "reason") ?? (GameEvents.Text(e, "by") is { } by ? $"player_stop:{by}" : GameEvents.Type(e));

    private async Task FinishAsync(OpenRound round)
    {
        if (Interlocked.Exchange(ref round.Finalized, 1) == 1) return;
        // Stays in _rounds (pending in the status) until its files are sent.
        RoundRecord record;
        List<JsonObject> events;
        List<AgentLink> links;
        lock (_lock)
        {
            record = round.Record;
            events = round.Events.Select(e => e.DeepClone().AsObject()).ToList();
            links = record.Players.Select(p => _agents.GetValueOrDefault(p.PlayerId)).OfType<AgentLink>().ToList();
            if (round.Server is { } id && _gameServers.TryGetValue(id, out var server)) links.Add(server);
        }
        var files = new RoundFiles(record.SessionId, record.ToJson(), RoundRecord.EventsJsonl(events), _profileJson);
        foreach (var link in links) await SendAsync(link, files);
        lock (_lock)
        {
            round.Room.Recorded.Add(record);
            Save(round, "finished");
            _rounds.Remove(record.SessionId);
        }
        log.LogInformation("round {Session}: {Kept}/{Count} players kept their recording", record.SessionId,
            record.Players.Count(p => p.Kept), record.Players.Count);
        RoundRecorded?.Invoke(files);
    }
}
