using System.Net.WebSockets;
using System.Text.Json.Nodes;
using Odx.Protocol;

namespace Odx.Orchestrator;

public sealed partial class MatchCoordinator
{
    private sealed class AgentLink(Hello hello, WebSocket socket)
    {
        public Hello Hello { get; } = hello;
        public string Id => Hello.PlayerId;
        public WebSocket Socket { get; } = socket;
        public SemaphoreSlim SendLock { get; } = new(1, 1);
        public string State { get; set; } = "idle";
        /// <summary>What is going wrong with its recording (<see cref="AgentState.Problem"/>); null while all is well or idle.</summary>
        public string? Problem { get; set; }
        public bool Ready { get; set; }
        public Room? Room { get; set; }
        /// <summary>ZeroTier node authorized on the room's LAN.</summary>
        public string? LanNode { get; set; }
    }

    private sealed class Room(string code, string? hostPlayerId, string? server = null)
    {
        public string Code { get; } = code;
        /// <summary>The game server agent recording this room's rounds; null: the only one connected, if just one is.</summary>
        public string? Server { get; } = server;
        /// <summary>Null for a room opened over HTTP (game server plugin, simulator): its round_start/round_end drive it.</summary>
        public string? HostPlayerId { get; } = hostPlayerId;
        public List<AgentLink> Members { get; } = [];
        public string? MatchId { get; set; }
        public string? Map { get; set; }
        public string Game { get; set; } = "";
        public int RoundNumber { get; set; }
        public OpenRound? Current { get; set; }
        /// <summary>Events between rounds (match_start, player_ready…); they open the next round's events.</summary>
        public List<JsonObject> BetweenRounds { get; } = [];
        public List<RoundRecord> Recorded { get; } = [];
        public DateTime EmptySinceUtc { get; set; } = DateTime.UtcNow;
        /// <summary>The room's ZeroTier network (virtual LAN); null without one.</summary>
        public string? LanNetworkId { get; init; }

        public bool IsHost(AgentLink a) => a.Id == HostPlayerId;
    }

    private sealed class OpenRound(Room room, RoundRecord record)
    {
        public Room Room { get; } = room;
        public RoundRecord Record { get; set; } = record;
        public List<JsonObject> Events { get; } = [];
        public bool Counting { get; set; }
        public bool Ended { get; set; }
        public int Finalized;
        /// <summary>The game server agent recording this round; it is done once it kept its side (or failed to).</summary>
        public string? Server { get; init; }
        public bool ServerDone { get; set; }
        /// <summary>Players who lost their connection while recording it, kept until <see cref="OrchestratorOptions.ReconnectGrace"/>.</summary>
        public HashSet<string> Away { get; } = [];

        public int IndexOf(string playerId) => Record.Players.FindIndex(p => p.PlayerId == playerId);
    }

    /// <summary>What a state change asks for once the lock is released.</summary>
    private sealed class Effects
    {
        public List<(AgentLink, AgentMessage)> Sends { get; } = [];
        public List<OpenRound> Ended { get; } = [];
        public List<OpenRound> Cancelled { get; } = [];
        public OpenRound? Countdown { get; set; }
        public HashSet<Room> Changed { get; } = [];
        public List<(string Network, string Node)> LanRevoke { get; } = [];
        public List<string> LanDelete { get; } = [];
    }
}
