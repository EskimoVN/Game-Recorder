using System.Net.Sockets;
using Odx.Core;

namespace Odx.Capture;

/// <summary>
/// Where a round's start falls on this machine's QPC clock, for odx record's grid origin (every player's frame k is then
/// round_start + k/fps: document/video-alignment-plan.md §3.1). Measured against the orchestrator's time service right
/// before the recorder starts, not read off the Windows clock (time-sync.md: hundreds of ms off, stepped at will): a burst
/// of samples, the lowest RTT wins, error within ±RTT/2. With no usable sample the grid runs free, as for a solo capture,
/// and the build reports it.
/// </summary>
public static class GridOrigin
{
    public const int Samples = 5;
    /// <summary>The sync.clock limit (time-sync.md §4): a sample slower than this says too little about the offset.</summary>
    public const double MaxRttMs = 100;
    public static readonly TimeSpan SampleTimeout = TimeSpan.FromMilliseconds(400);

    public const string Aligned = Odx.Protocol.CaptureReport.Aligned;

    /// <returns>The origin (null when the grid runs free), the sample's RTT (null without an answer) and a note:
    /// <see cref="Aligned"/> or why not.</returns>
    public static (double? OriginQpcMs, double? RttMs, string Note) Measure(string timeServer, double roundStartUtcMs)
    {
        NtpSample? best = null;
        string? error = null;
        for (int i = 0; i < Samples; i++)
        {
            try
            {
                var s = SntpClient.Query(timeServer, SampleTimeout);
                if (best is null || s.RttMs < best.RttMs) best = s;
            }
            catch (Exception e) when (e is SocketException or InvalidDataException or IOException)
            {
                error = e.Message;
            }
        }
        if (best is null) return (null, null, $"time service silent ({error})");
        if (best.RttMs > MaxRttMs) return (null, best.RttMs, $"time service RTT {best.RttMs:F0} ms, over {MaxRttMs:F0}");
        return (best.TQpcMs + (roundStartUtcMs - best.UtcUnixMs), best.RttMs, Aligned);
    }
}
