namespace Odx.Orchestrator;

public sealed record OrchestratorOptions(string GameProfilePath, int HttpPort = 5180, int TimePort = 12300, int Fps = 30)
{
    /// <summary>How long after a round ends to wait for every participant's Done before sending the round's files anyway.</summary>
    public TimeSpan DoneTimeout { get; init; } = TimeSpan.FromMinutes(15);
    /// <summary>
    /// How long an agent that lost its connection keeps its place in a running round (recording on, on its own machine)
    /// before it is stopped there; the agent stops by itself after as long.
    /// </summary>
    public TimeSpan ReconnectGrace { get; init; } = TimeSpan.FromSeconds(30);
    /// <summary>The SQLite file keeping rooms and rounds across restarts (<see cref="OrchestratorStore"/>); null keeps none.</summary>
    public string? StatePath { get; init; }
    /// <summary>A room nobody is in is closed after this long (the host closes it otherwise).</summary>
    public TimeSpan EmptyRoomTimeout { get; init; } = TimeSpan.FromMinutes(30);
    /// <summary>Every HTTP and WebSocket request must carry "Authorization: Bearer &lt;key&gt;"; null lets anyone in.</summary>
    public string? AccessKey { get; init; }
    /// <summary>ZeroTier controller API (and its token) for rooms with a virtual LAN; null: rooms have no LAN.</summary>
    public Uri? ZeroTierApi { get; init; }
    public string? ZeroTierToken { get; init; }
    /// <summary>NTP server this machine's clock is measured against every minute (spec v3 §4 layer 1); null takes
    /// time_server of odx-capture.json.</summary>
    public string? TimeServer { get; init; }
}
