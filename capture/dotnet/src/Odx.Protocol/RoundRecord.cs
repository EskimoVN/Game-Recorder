using System.Text.Json;
using System.Text.Json.Nodes;

namespace Odx.Protocol;

/// <summary>The orchestrator's own clock against the configured NTP server (spec v3 §4 baseline: players and server).</summary>
/// <param name="OffsetMs">NTP time − orchestrator clock.</param>
public sealed record ServerClock(string Server, double OffsetMs, double RttMs, double MeasuredAtUtcMs);

/// <param name="StopReason">"round_end", "permanent_death" or "abort"; null while recording.</param>
/// <param name="StopUtcMs">Server UTC when this player's recording was stopped.</param>
/// <param name="SteamId">The Steam account the player was signed in with (SteamID64), which the game server's state names.</param>
/// <param name="Report">The recording's numbers (<see cref="Done.Report"/>); null from an agent that sent none.</param>
/// <param name="Verdict"><see cref="CaptureReport.Ok"/>, <see cref="CaptureReport.Degraded"/> or <see cref="CaptureReport.Failed"/>:
/// whether the host should record the round again for this player. Null while not done.</param>
/// <param name="Problems">The report's problems, one line each; empty when ok.</param>
public sealed record RoundPlayer(string PlayerId, string Name, string? StopReason = null, double? StopUtcMs = null,
    bool Done = false, bool Kept = false, string? Error = null, string? ToolVersion = null, string? MachineId = null,
    string? SteamId = null, CaptureReport? Report = null, string? Verdict = null, List<string>? Problems = null);

/// <summary>
/// One round as the orchestrator saw it (staging round.json). One round = one session (spec v3 §1: task boundaries are
/// rounds). <c>StartUtcMs</c> is when play starts: the round_start event plus its countdown, during which players already
/// record. <c>TimePort</c> is the UDP port of the orchestrator's NTP responder the players measured against; the host part
/// of their samples' server differs with how each agent reaches the orchestrator.
/// </summary>
public sealed record RoundRecord(string SessionId, string MatchId, string Game, string? Map, int Round, double StartUtcMs,
    double? EndUtcMs, int TimePort, List<RoundPlayer> Players, int CountdownMs = 0, string? EndReason = null,
    ServerClock? ServerClock = null)
{
    public const string FileName = "round.json", EventsFileName = "server_events.jsonl", ProfileFileName = "game_profile.json";

    public static RoundRecord Load(string stagingDir) =>
        JsonSerializer.Deserialize<RoundRecord>(File.ReadAllText(Path.Combine(stagingDir, FileName)), AgentMessage.Json)
        ?? throw new InvalidDataException("empty round.json");

    public void Save(string stagingDir) => File.WriteAllText(Path.Combine(stagingDir, FileName), ToJson());

    public string ToJson() => JsonSerializer.Serialize(this, PrettyJson);

    public static string EventsJsonl(IEnumerable<JsonObject> events) =>
        string.Concat(events.Select(e => e.ToJsonString(AgentMessage.Json) + "\n"));

    public static readonly JsonSerializerOptions PrettyJson = new(AgentMessage.Json) { WriteIndented = true };
}
