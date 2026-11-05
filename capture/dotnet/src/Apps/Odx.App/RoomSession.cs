using Odx.Agent;
using Odx.Capture;
using Odx.Core;
using Odx.Protocol;

namespace Odx.App;

/// <summary>
/// One player's time in a room, the same for the window and for --headless: the agent and its connection, the packet capture
/// its game asks for, and what a status line shows (last log line, LAN, packets). Events come from background threads.
/// </summary>
public sealed class RoomSession : IDisposable
{
    private readonly CancellationTokenSource _stop = new();
    private readonly Func<Task<IPacketCapture?>> _packets;

    /// <param name="packets">Gives the packet capture when the room's game wants one; null when it cannot be had.</param>
    public RoomSession(AgentOptions options, Func<CaptureOptions, ICaptureRun> startCapture, Func<Task<IPacketCapture?>> packets)
    {
        _packets = packets;
        Agent = new AgentClient(options, startCapture);
        Agent.Log += line =>
        {
            LastLog = $"{DateTime.Now:HH:mm:ss} {line}";
            Changed?.Invoke();
        };
        Agent.LanChanged += lan =>
        {
            Lan = lan;
            Changed?.Invoke();
            if (lan is { Status: "zerotier_missing" }) LanMissing?.Invoke();
        };
        Agent.PacketCaptureWanted += wanted => _ = WantPacketsAsync();
        Agent.ClockChecked += _ => Changed?.Invoke();
    }

    /// <summary>Measures the clock again (after a sync); null when not connected.</summary>
    public async Task<ClockCheck?> CheckClockAsync()
    {
        try
        {
            return await Agent.CheckClockAsync(_stop.Token);
        }
        catch (OperationCanceledException)
        {
            return null;
        }
    }

    public AgentClient Agent { get; }

    public string LastLog { get; private set; } = "";

    public LanState? Lan { get; private set; }

    /// <summary>null: the room's game records no packets; true/false: they are recorded, or the capture could not be had.</summary>
    public bool? Packets { get; private set; }

    /// <summary>Anything a status line shows has changed.</summary>
    public event Action? Changed;

    /// <summary>The room plays over a LAN this machine cannot join yet (ZeroTier is missing).</summary>
    public event Action? LanMissing;

    /// <summary>Runs until <see cref="Dispose"/>, reconnecting as <see cref="AgentClient.RunUntilStoppedAsync"/> does.</summary>
    // Off the caller's thread: a WPF caller's continuations would wait for the dispatcher it blocks.
    public Task RunAsync() => Task.Run(() => Agent.RunUntilStoppedAsync(_stop.Token));

    /// <summary>Once per agent: a reconnect asks again, and the capture it got stays.</summary>
    private async Task WantPacketsAsync()
    {
        if (Agent.Packets is not null) return;
        Agent.Packets = await _packets();
        Packets = Agent.Packets is not null;
        Changed?.Invoke();
    }

    public Task LeaveAsync() => Quietly(() => Agent.LeaveRoomAsync(_stop.Token));

    public Task CloseAsync() => Quietly(() => Agent.CloseRoomAsync(_stop.Token));

    public Task ToggleReadyAsync() => Quietly(() => Agent.SetReadyAsync(!Agent.IsReady, _stop.Token));

    /// <exception cref="System.Net.WebSockets.WebSocketException">Not connected.</exception>
    public Task ToggleRoundAsync(int countdownMs) => Agent.ToggleRoundAsync(countdownMs, _stop.Token);

    public Task JoinLanAsync() => Agent.JoinLanAsync(_stop.Token);

    /// <summary>Disconnects for good; a recording in progress stops.</summary>
    public void Dispose() => _stop.Cancel();

    private static async Task Quietly(Func<Task> request)
    {
        try
        {
            await request();
        }
        catch (Exception e) when (e is System.Net.WebSockets.WebSocketException or OperationCanceledException)
        {
        }
    }
}
