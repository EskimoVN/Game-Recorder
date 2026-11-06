using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Odx.Core;
using Odx.Protocol;

namespace Odx.Orchestrator;

public sealed partial class MatchCoordinator
{
    private void Join(AgentLink link, Room room, Effects effects)
    {
        room.Members.Add(link);
        link.Room = room;
        link.Ready = false;
        effects.Changed.Add(room);
        log.LogInformation("{Player} joined room {Room}", link.Id, room.Code);
    }

    /// <summary>Out of the room: a countdown they are in is cancelled, a recording of theirs is stopped.</summary>
    private void Leave(AgentLink link, string reason, Effects effects)
    {
        if (link.Room is not { } room) return;
        room.Members.Remove(link);
        link.Room = null;
        link.Ready = false;
        // "reconnected": this connection died after the agent's new one came in; the new one holds its LAN node and rounds.
        bool replaced = reason == "reconnected";
        if (!replaced && room.LanNetworkId is { } network && link.LanNode is { } node)
        {
            // A lost connection keeps the LAN for the grace: the agent says its node again when it is back.
            if (reason == "disconnected") _awayLan[link.Id] = (network, node);
            else effects.LanRevoke.Add((network, node));
        }
        link.LanNode = null;
        if (!replaced && room.Current is { } round && round.IndexOf(link.Id) is var i && i >= 0)
        {
            if (round.Counting) Cancel(round, $"player_left:{link.Id}", effects);
            else if (reason == "disconnected" && round.Record.Players[i].StopReason is null)
            {
                round.Away.Add(link.Id);
                log.LogWarning("{Player} lost the connection in {Session}; kept for {Grace} s", link.Id, round.Record.SessionId,
                    options.ReconnectGrace.TotalSeconds);
            }
            else StopPlayer(round, link.Id, NtpResponder.NowUnixMs(), reason, effects);
        }
        if (room.Members.Count == 0) room.EmptySinceUtc = DateTime.UtcNow;
        effects.Changed.Add(room);
        log.LogInformation("{Player} left room {Room} ({Reason})", link.Id, room.Code, reason);
    }

    private void Close(Room room, Effects effects)
    {
        if (room.Current is { Counting: true } counting) Cancel(counting, "room_closed", effects);
        else if (room.Current is not null) EndRound(room, NtpResponder.NowUnixMs(), "round_end", "room_closed", effects);
        foreach (var member in room.Members)
        {
            member.Room = null;
            member.Ready = false;
            member.LanNode = null;
            effects.Sends.Add((member, new RoomClosed(room.Code)));
        }
        room.Members.Clear();
        _rooms.Remove(room.Code);
        if (room.LanNetworkId is { } network) effects.LanDelete.Add(network);
        log.LogInformation("room {Room} closed", room.Code);
    }

    private string NewCode()
    {
        string code;
        do code = new string(RandomNumberGenerator.GetItems<char>(CodeAlphabet, 6));
        while (_rooms.ContainsKey(code));
        return code;
    }

    /// <summary>A room without a host, for a game server plugin or the simulator; returns its invite code.</summary>
    /// <param name="server">The game server agent that records its rounds; null: the only one connected.</param>
    public string OpenRoom(string? server = null)
    {
        lock (_lock)
        {
            var room = new Room(NewCode(), null, server);
            _rooms[room.Code] = room;
            var opened = new Effects();
            opened.Changed.Add(room);
            Persist(opened);
            log.LogInformation("room {Room} opened (no host{Server})", room.Code, server is null ? "" : $", game server {server}");
            return room.Code;
        }
    }

    /// <summary>Called with the lock held.</summary>
    private AgentLink? ServerFor(Room room) =>
        room.Server is { } id ? _gameServers.GetValueOrDefault(id) : _gameServers.Count == 1 ? _gameServers.Values.Single() : null;

    /// <summary>Closes rooms that stayed empty for <see cref="OrchestratorOptions.EmptyRoomTimeout"/>.</summary>
    public async Task SweepRoomsAsync(CancellationToken stop)
    {
        while (!stop.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromMinutes(1), stop);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            lock (_lock)
                foreach (var room in _rooms.Values.Where(r => r.Members.Count == 0 && r.Current is null &&
                                                              DateTime.UtcNow - r.EmptySinceUtc > options.EmptyRoomTimeout).ToList())
                {
                    _rooms.Remove(room.Code);
                    Store?.CloseRoom(room.Code);
                    if (room.LanNetworkId is { } network) _ = DeleteLanAsync(network);
                    log.LogInformation("room {Room} closed after staying empty", room.Code);
                }
        }
    }

    private void SetReady(Room room, AgentLink link, bool ready, Effects effects)
    {
        link.Ready = ready;
        effects.Changed.Add(room);
        Apply(room, new JsonObject { ["type"] = ready ? "player_ready" : "player_unready", ["player"] = link.Id,
            ["name"] = link.Hello.Name }, effects);
        if (!ready && room.Current is { Counting: true } counting && counting.IndexOf(link.Id) >= 0)
            Cancel(counting, $"player_unready:{link.Id}", effects);
    }

    /// <summary>Members other than the host who are not ready (or still busy with the last round).</summary>
    private static List<AgentLink> NotReady(Room room) =>
        room.Members.Where(a => !room.IsHost(a) && !(a.Ready && a.State is "idle" or "armed")).ToList();

    /// <summary>A round of any room, open, finished or stored, already has this session id (two sessions cannot share a folder).</summary>
    private bool SessionIdUsed(string sid) =>
        _rounds.ContainsKey(sid) || _rooms.Values.Any(r => r.Recorded.Any(x => x.SessionId == sid)) || Store?.Round(sid) is not null;

    /// <summary>Members, the host included, whose capture of the last round is not done: a new round would leave them out.</summary>
    private static List<AgentLink> Saving(Room room) => room.Members.Where(a => a.State is "starting" or "recording" or "saving").ToList();

    /// <summary>A ZeroTier network for a new room; null (and the host told why) when this server cannot make one.</summary>
    private async Task<string?> CreateLanAsync(AgentLink link, CancellationToken stop)
    {
        if (_zeroTier is null)
        {
            await SendAsync(link, new RoomError("this server has no virtual LAN (ZeroTier) set up"));
            return null;
        }
        try
        {
            return await _zeroTier.CreateNetworkAsync("odx-room", stop);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
        {
            log.LogError("ZeroTier controller: {Error}", e.Message);
            await SendAsync(link, new RoomError($"cannot create the room's LAN: {e.Message}"));
            return null;
        }
    }

    private async Task AuthorizeLanAsync(AgentLink link, string nodeId, CancellationToken stop)
    {
        string? network;
        lock (_lock)
        {
            network = link.Room?.LanNetworkId;
            if (network is null || !ZeroTierApi.IsNodeId(nodeId)) return;
            link.LanNode = nodeId;
        }
        try
        {
            await _zeroTier!.AuthorizeAsync(network, nodeId, true, stop);
            log.LogInformation("{Player} ({Node}) on the LAN of room {Room}", link.Id, nodeId, link.Room?.Code);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
        {
            log.LogError("ZeroTier controller: {Error}", e.Message);
            await SendAsync(link, new RoomError($"cannot add you to the room's LAN: {e.Message}"));
        }
    }

    private async Task DeleteLanAsync(string network)
    {
        try
        {
            await _zeroTier!.DeleteNetworkAsync(network, default);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
        {
            log.LogWarning("ZeroTier controller: {Error}", e.Message);
        }
    }
}
