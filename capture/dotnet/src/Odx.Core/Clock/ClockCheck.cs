using System.Text.Json;
using System.Text.Json.Serialization;

namespace Odx.Core;

/// <summary>
/// Spec v3 §4 layer 1 ("OS clock via NTP on all machines"), measured before a round: this machine's Windows clock against
/// a real NTP server, and against the orchestrator's time service (layer 2's reference). Written to the round folder as
/// clock_check.json and carried into player.json → clock_sync.system_clock.
/// </summary>
/// <param name="NtpOffsetMs">NTP server − Windows clock (the lowest-RTT sample); null when the server did not answer.</param>
/// <param name="ReferenceOffsetMs">Orchestrator time service − Windows clock; null when it did not answer.</param>
/// <param name="OrchestratorNtpOffsetMs">The orchestrator's own clock against NTP (its last measurement); null if it has none.</param>
/// <param name="SyncedAtUtcMs">When the Windows clock was set (odx clock-sync) before this measurement, if it was.</param>
public sealed record ClockCheck(string? NtpServer, double? NtpOffsetMs, double? NtpRttMs, string? NtpError,
    string? Reference, double? ReferenceOffsetMs, double? ReferenceRttMs, string? ReferenceError,
    double? OrchestratorNtpOffsetMs, double? OrchestratorNtpMeasuredAtUtcMs, double MeasuredAtUtcMs, double? SyncedAtUtcMs = null)
{
    public const string FileName = "clock_check.json";

    /// <summary>
    /// A Windows clock further than this from NTP fails layer 1 (proposal: spec v3 §4 leaves the tolerance open; one frame
    /// at 30 fps is 33 ms, public NTP over the internet is good to ~10 ms). capture/README.md "Decisions pending".
    /// </summary>
    public const double MaxOffsetMs = 50;

    private const int Samples = 8;
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(2);
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>Layer 1 holds: the Windows clock is within <see cref="MaxOffsetMs"/> of NTP.</summary>
    [JsonIgnore]
    public bool Ok => NtpOffsetMs is double o && Math.Abs(o) <= MaxOffsetMs;

    /// <summary>Queries both servers (<see cref="Samples"/> times each, keeping the lowest-RTT sample); blocks for up to a few seconds.</summary>
    /// <param name="ntpServer">A real NTP server (time_server of odx-capture.json); null measures the reference only.</param>
    /// <param name="reference">The orchestrator's time service ("host:port"); null measures NTP only.</param>
    public static ClockCheck Measure(string? ntpServer, string? reference)
    {
        var (ntp, ntpError) = ntpServer is null ? (null, null) : Best(ntpServer);
        var (reference_, referenceError) = reference is null ? (null, null) : Best(reference);
        return new ClockCheck(ntpServer, ntp?.SystemOffsetMs, ntp?.RttMs, ntpError,
            reference, reference_?.SystemOffsetMs, reference_?.RttMs, referenceError,
            null, null, NtpResponder.NowUnixMs());
    }

    private static (NtpSample? Best, string? Error) Best(string server)
    {
        NtpSample? best = null;
        string? error = null;
        for (int i = 0; i < Samples; i++)
        {
            try
            {
                var s = SntpClient.Query(server, Timeout);
                if (best is null || s.RttMs < best.RttMs) best = s;
            }
            catch (Exception e) when (e is System.Net.Sockets.SocketException or InvalidDataException)
            {
                error = e.Message;
            }
        }
        return (best, best is null ? error : null);
    }

    public void Save(string path) => File.WriteAllText(path, JsonSerializer.Serialize(this, Json));

    public static ClockCheck Load(string path) =>
        JsonSerializer.Deserialize<ClockCheck>(File.ReadAllText(path), Json) ?? throw new InvalidDataException($"{path}: empty");
}
