using System.Net.WebSockets;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Odx.Protocol;

/// <summary>
/// One event from the game server (POST /game/events), game-agnostic: a game's server plugin maps its own events to
/// these types. They drive recording (round boundaries, permanent deaths) and are the sync points of spec v3 §3.2.
/// Extra fields (attacker, weapon, map…) are kept as sent and written to events.jsonl.
/// </summary>
public static class GameEvents
{
    public const string MatchStart = "match_start";      // game, map
    public const string RoundStart = "round_start";      // round, countdown_ms (host-started rounds)
    public const string RoundEnd = "round_end";          // round, winner?
    public const string Kill = "kill";                   // attacker, victim, weapon
    public const string PlayerDeath = "player_death";    // player, permanent (cannot respawn this round)
    public const string PlayerConnect = "player_connect";
    public const string PlayerDisconnect = "player_disconnect";
    public const string MatchEnd = "match_end";

    public static string Type(JsonObject e) => e["type"]?.GetValue<string>() ?? throw new FormatException("event without \"type\"");

    /// <summary>Server UTC of the event; the orchestrator stamps events that arrive without one.</summary>
    public static double UtcMs(JsonObject e) => e["utc_ms"]?.GetValue<double>() ?? throw new FormatException("event without \"utc_ms\"");

    public static string? Text(JsonObject e, string key) => e[key] is JsonValue v && v.TryGetValue(out string? s) ? s : null;
}

/// <summary>
/// WebSocket messages between an agent and the orchestrator, one JSON object each. An agent is a player's capture machine or
/// the game server's (<see cref="Hello.Role"/>).
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(Hello), "hello")]
[JsonDerivedType(typeof(Welcome), "welcome")]
[JsonDerivedType(typeof(Arm), "arm")]
[JsonDerivedType(typeof(Start), "start")]
[JsonDerivedType(typeof(Stop), "stop")]
[JsonDerivedType(typeof(AgentState), "state")]
[JsonDerivedType(typeof(Done), "done")]
[JsonDerivedType(typeof(Ready), "ready")]
[JsonDerivedType(typeof(CreateRoom), "create_room")]
[JsonDerivedType(typeof(JoinRoom), "join_room")]
[JsonDerivedType(typeof(LeaveRoom), "leave_room")]
[JsonDerivedType(typeof(CloseRoom), "close_room")]
[JsonDerivedType(typeof(ToggleRound), "toggle_round")]
[JsonDerivedType(typeof(RoomUpdate), "room")]
[JsonDerivedType(typeof(RoomError), "room_error")]
[JsonDerivedType(typeof(RoomClosed), "room_closed")]
[JsonDerivedType(typeof(LanMember), "lan_member")]
[JsonDerivedType(typeof(RoundFiles), "round_record")]
[JsonDerivedType(typeof(GameEvent), "game_event")]
[JsonDerivedType(typeof(Incompatible), "incompatible")]
public abstract record AgentMessage
{
    /// <summary>Raised whenever a message changes in a way the other side cannot read; sent in <see cref="Hello"/>.</summary>
    public const int Version = 3;

    public static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public string ToJson() => JsonSerializer.Serialize(this, Json);

    public static AgentMessage Parse(string json) =>
        JsonSerializer.Deserialize<AgentMessage>(json, Json) ?? throw new JsonException("empty message");
}

/// <summary>Agent → orchestrator, first message.</summary>
/// <param name="MachineId">A random id made once per installation (data\machine-id), so machines cloned from one image or
/// sharing a name stay apart.</param>
/// <param name="SteamId">The Steam account signed in on the machine (SteamID64), to match the player in the game server's
/// state; null without Steam.</param>
/// <param name="Role"><see cref="PlayerRole"/>, or <see cref="GameServerRole"/>: the game server's agent, which records the
/// server's side of every round (Start / Stop, then Done) and joins no room.</param>
/// <param name="Recording">Sessions this agent is still recording, after a lost connection: the orchestrator sends the Stop
/// it missed, or lets it go on.</param>
/// <param name="Protocol">The agent's <see cref="AgentMessage.Version"/>; 0 from agents older than the field.</param>
public sealed record Hello(string PlayerId, string Name, string ToolVersion, string? MachineId = null, string? SteamId = null,
    string Role = Hello.PlayerRole, List<string>? Recording = null, int Protocol = 0) : AgentMessage
{
    public const string PlayerRole = "player", GameServerRole = "game_server";
}

/// <summary>Orchestrator → agent, instead of <see cref="Welcome"/>: they speak different protocol versions; the agent needs
/// the same release as the orchestrator, and connecting again cannot help.</summary>
public sealed record Incompatible(int Protocol, string OrchestratorVersion) : AgentMessage;

/// <summary>What an agent throws on <see cref="Incompatible"/>: it stops instead of reconnecting.</summary>
public sealed class IncompatibleException(Incompatible answer) : Exception(
    $"the orchestrator ({answer.OrchestratorVersion}) speaks protocol {answer.Protocol}, this one {AgentMessage.Version}: install the same release");

public static class WebSocketText
{
    /// <summary>One whole text message; null once the connection is closed, broken or given up by the keep-alive.</summary>
    public static async Task<string?> ReceiveAsync(WebSocket socket, byte[] buffer, CancellationToken stop)
    {
        using var message = new MemoryStream();
        while (true)
        {
            WebSocketReceiveResult r;
            try
            {
                r = await socket.ReceiveAsync(buffer, stop);
            }
            catch (WebSocketException)
            {
                return null;
            }
            catch (OperationCanceledException) when (!stop.IsCancellationRequested)
            {
                return null;  // the keep-alive gave up on the peer
            }
            if (r.MessageType == WebSocketMessageType.Close) return null;
            message.Write(buffer, 0, r.Count);
            if (r.EndOfMessage) return Encoding.UTF8.GetString(message.ToArray());
        }
    }
}

/// <summary>
/// Orchestrator → agent: where to measure the clock (UDP port of the orchestrator's NTP responder) and the game profile its
/// rounds use, so a player knows before any round whether packets will be captured.
/// </summary>
/// <param name="ReconnectGraceMs">How long a round keeps an agent that lost its connection; it records on meanwhile and
/// stops by itself once this has passed without a reconnect.</param>
/// <param name="ServerClock">The orchestrator's clock against its NTP server (spec v3 §4 layer 1 on the reference side);
/// null while it has not measured it.</param>
public sealed record Welcome(int TimePort, string? GameProfileJson = null, int ReconnectGraceMs = 30_000,
    ServerClock? ServerClock = null) : AgentMessage;

/// <summary>Orchestrator → agent: prepare a session; the game profile (JSON) is the one the build must use.</summary>
public sealed record Arm(string SessionId, string GameProfileJson, int Fps) : AgentMessage;

/// <summary>
/// Orchestrator → agent: start capturing now; play starts after <paramref name="CountdownMs"/> (the countdown players see
/// when a host starts the round; 0 when the game server does). Not scheduled for a common instant: capture start-up takes
/// 0.5–3.5 s, and every frame is put on server time afterwards anyway.
/// </summary>
public sealed record Start(string SessionId, int CountdownMs = 0) : AgentMessage;

/// <summary>Orchestrator → agent: stop capturing ("round_end", "permanent_death", "abort", or <see cref="Cancelled"/>: discard).</summary>
public sealed record Stop(string SessionId, string Reason) : AgentMessage
{
    public const string Cancelled = "cancelled";
}

/// <summary>Agent → orchestrator: idle, armed, starting, recording, saving (stop to Done), error.</summary>
public sealed record AgentState(string State, string? Detail = null) : AgentMessage;

/// <summary>
/// Agent → orchestrator: the session's files are kept (or why not). Sent again after every reconnect until the round's
/// files (<see cref="RoundFiles"/>) arrive, since one sent while the connection was lost goes nowhere.
/// </summary>
public sealed record Done(string SessionId, bool Ok, string? Error = null) : AgentMessage;

/// <summary>Agent → orchestrator: the player is (not) ready; when every player of the room is, a round starts after a countdown.</summary>
public sealed record Ready(bool IsReady) : AgentMessage;

/// <summary>
/// Agent → orchestrator: open a room with this player as its host; the answer is a <see cref="RoomUpdate"/> with the invite
/// code. <paramref name="Lan"/>: give the room a virtual LAN (a ZeroTier network) for games that only play over LAN.
/// </summary>
public sealed record CreateRoom(bool Lan = false) : AgentMessage;

/// <summary>Agent → orchestrator: join the room with this invite code (leaving any other).</summary>
public sealed record JoinRoom(string Code) : AgentMessage;

public sealed record LeaveRoom : AgentMessage;

/// <summary>Agent → orchestrator, host only: close the room for everyone.</summary>
public sealed record CloseRoom : AgentMessage;

/// <summary>Agent → orchestrator, host only: start a round (with this countdown) when none runs, otherwise end it.</summary>
/// <param name="SessionId">The round's session id, typed by the host: the name of every player's capture folder and of the
/// delivered session; null lets the orchestrator name it <c>&lt;match&gt;-rNN</c>.</param>
public sealed record ToggleRound(int CountdownMs, string? SessionId = null) : AgentMessage
{
    /// <summary>A session id is a folder name: letters, digits, <c>_ . -</c>, 64 characters at most, not starting with a dot or dash.</summary>
    public static bool IsValidSessionId(string id) => System.Text.RegularExpressions.Regex.IsMatch(id, "^[A-Za-z0-9][A-Za-z0-9_.-]{0,63}$");
}

public sealed record RoomMember(string PlayerId, string Name, bool Ready, string State);

/// <summary>Orchestrator → every member whenever the room changes (members, ready, states, round).</summary>
/// <param name="LanNetworkId">The room's ZeroTier network to join; null when the room has no LAN.</param>
public sealed record RoomUpdate(string Code, string? HostPlayerId, List<RoomMember> Members, string? CurrentSession,
    bool Countdown, string? LanNetworkId = null) : AgentMessage;

/// <summary>Orchestrator → agent: a room request failed (unknown code, not the host…).</summary>
public sealed record RoomError(string Message) : AgentMessage;

/// <summary>Orchestrator → members: the host closed the room.</summary>
public sealed record RoomClosed(string Code) : AgentMessage;

/// <summary>Agent → orchestrator: this machine (ZeroTier node) joined the room's LAN; the orchestrator authorizes it.</summary>
public sealed record LanMember(string NodeId) : AgentMessage;

/// <summary>
/// Orchestrator → each member of the room, as soon as a game event is stamped: the player's machine notes when it arrived
/// (local_receive_* in its events.jsonl, as in the client's multiplayer delivery).
/// </summary>
public sealed record GameEvent(long EventId) : AgentMessage;

public sealed record RoundFiles(string SessionId, string Round, string Events, string GameProfile) : AgentMessage
{
    public void WriteTo(string dir)
    {
        File.WriteAllText(Path.Combine(dir, RoundRecord.FileName), Round);
        File.WriteAllText(Path.Combine(dir, RoundRecord.EventsFileName), Events);
        File.WriteAllText(Path.Combine(dir, RoundRecord.ProfileFileName), GameProfile);
    }
}
