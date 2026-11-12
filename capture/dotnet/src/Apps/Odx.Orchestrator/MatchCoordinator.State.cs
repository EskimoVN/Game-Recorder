using Odx.Protocol;

namespace Odx.Orchestrator;

public sealed partial class MatchCoordinator
{
    /// <summary>
    /// Takes back what the store kept from before a restart: the open rooms (empty until their members reconnect) and their
    /// unfinished rounds. Everyone in those rounds counts as away, with the usual grace to come back; a round that was
    /// counting down is cancelled, since its countdown cannot be resumed.
    /// </summary>
    public void Restore()
    {
        if (_store is null) return;
        var gone = new List<(string Id, long Absence, bool Server)>();
        var ended = new List<OpenRound>();
        lock (_lock)
        {
            _eventId = _store.NextEventId;
            foreach (var r in _store.OpenRooms())
            {
                var room = new Room(r.Code, r.Host, r.Server)
                    // Rooms from before protocol 5 have no game until their first match: they play the default.
                    { LanNetworkId = r.LanNetworkId, MatchId = r.MatchId, Map = r.Map, Game = r.Game is "" ? _defaultGame : r.Game, RoundNumber = r.RoundNumber };
                room.BetweenRounds.AddRange(r.BetweenRounds);
                _rooms[room.Code] = room;
            }
            foreach (var s in _store.UnfinishedRounds())
            {
                if (!_rooms.TryGetValue(s.Room, out var room)) continue;
                if (s.Counting)
                {
                    _store.SaveRound(s with { State = "cancelled" });
                    room.RoundNumber = s.Record.Round - 1;
                    continue;
                }
                var round = new OpenRound(room, s.Record) { Server = s.Server, ServerDone = s.ServerDone, Ended = s.State == "ended" };
                round.Events.AddRange(s.Events);
                _rounds[s.SessionId] = round;
                if (round.Ended) ended.Add(round);
                else room.Current = round;
                foreach (var p in round.Record.Players.Where(p => !p.Done))
                {
                    if (p.StopReason is null) round.Away.Add(p.PlayerId);
                    gone.Add((p.PlayerId, _absent[p.PlayerId] = ++_absences, false));
                }
                if (round.Server is { } server && !round.ServerDone) gone.Add((server, _absent[server] = ++_absences, true));
            }
            log.LogInformation("restored {Rooms} room(s) and {Rounds} unfinished round(s) from {Store}", _rooms.Count, _rounds.Count,
                options.StatePath);
        }
        foreach (var (id, absence, server) in gone)
            _ = Task.Delay(options.ReconnectGrace).ContinueWith(_ => server ? Task.Run(() => GameServerGone(id, absence)) : PlayerGoneAsync(id, absence),
                TaskScheduler.Default).Unwrap();
        foreach (var round in ended)
            _ = Task.Delay(options.DoneTimeout).ContinueWith(_ => FinishAsync(round), TaskScheduler.Default).Unwrap();
    }

    /// <summary>Called with the lock held, after every change.</summary>
    private void Persist(Effects effects)
    {
        if (Store is not { } store) return;
        store.NextEventId = _eventId;
        // Cancelled first: a round_start during a countdown cancels the round and starts one with the same session id.
        foreach (var round in effects.Cancelled) Save(round, "cancelled");
        foreach (var room in effects.Changed)
        {
            if (!_rooms.ContainsKey(room.Code))
            {
                store.CloseRoom(room.Code);
                continue;
            }
            store.SaveRoom(new StoredRoom(room.Code, room.HostPlayerId, room.Server, room.LanNetworkId, room.MatchId, room.Map,
                room.Game, room.RoundNumber, room.BetweenRounds));
            foreach (var round in _rounds.Values.Where(r => r.Room == room)) Save(round);
        }
    }

    /// <summary>Called with the lock held.</summary>
    private void Save(OpenRound round, string? state = null) =>
        Store?.SaveRound(new StoredRound(round.Record.SessionId, round.Room.Code, state ?? (round.Ended ? "ended" : "open"),
            round.Counting, round.Server, round.ServerDone, round.Record, round.Events));

    public void Dispose()
    {
        lock (_lock)
        {
            _disposed = true;
            _store?.Dispose();
        }
    }
}
