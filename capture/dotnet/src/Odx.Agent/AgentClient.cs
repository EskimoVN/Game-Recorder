using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Odx.Capture;
using Odx.Core;
using Odx.Protocol;

namespace Odx.Agent;

/// <param name="Orchestrator">e.g. http://192.168.1.10:5180</param>
/// <param name="WindowProcess">Process name to record (odx record --window); null: the game profile's window, else the monitor.</param>
/// <param name="RoomCode">Invite code of the room to join; with <paramref name="CreateRoom"/> instead, a new room is opened
/// with this player as its host. After a reconnect the agent joins the same room again.</param>
/// <param name="AutoReady">Says ready whenever it is idle in a room between rounds (headless capture machines).</param>
/// <param name="AccessKey">The orchestrator's access key (access_key in odx-capture.json), sent with every request.</param>
/// <param name="Lan">With <paramref name="CreateRoom"/>: the new room gets a virtual LAN (ZeroTier).</param>
/// <param name="TimeServer">A real NTP server (time_server of odx-capture.json) the Windows clock is checked against before
/// a round (spec v3 §4 layer 1): a clock further than <see cref="ClockCheck.MaxOffsetMs"/> off cannot be ready. Null skips
/// the check.</param>
public sealed record AgentOptions(Uri Orchestrator, string PlayerId, string Name, string OutputRoot, string? WindowProcess,
    string? RoomCode = null, bool CreateRoom = false, bool AutoReady = false, string? AccessKey = null, bool Lan = false,
    string? TimeServer = null);

/// <param name="Status">"joining", "ok", "zerotier_missing" (not installed, or its tray app never ran), "error".</param>
/// <param name="Address">This machine's IP on the room's LAN once "ok"; the error text for "error".</param>
public sealed record LanState(string NetworkId, string Status, string? Address = null);

/// <summary>
/// A player's capture machine under the orchestrator, in one room: on Start it records into &lt;OutputRoot&gt;/&lt;session&gt;/
/// (sampling its clock against the orchestrator's time service; its Windows clock is checked against NTP and the time
/// service on Welcome and on demand, <see cref="CheckClockAsync"/>), on Stop it lets the capture build
/// and validate the session and reports Done. The folder stays, and gets the round's files (<see cref="RoundFiles"/>) once
/// everyone is done, with a <see cref="Manifest"/>. odx-app runs this, in its room mode or headless.
/// </summary>
public sealed class AgentClient(AgentOptions options, Func<CaptureOptions, ICaptureRun> startCapture)
{
    private static readonly TimeSpan Retry = TimeSpan.FromSeconds(5);

    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private ClientWebSocket? _socket;
    private string? _timeServer;
    private ServerClock? _serverClock;
    private Arm? _armed;
    private string? _profilePath;
    private string? _profileWindow;
    private PacketCaptureSpec? _armedNetwork;
    private readonly object _gate = new();
    /// <summary>When each game event's notice arrived, on this machine's Windows clock (Unix ms), by event_id.</summary>
    private readonly ConcurrentDictionary<long, double> _eventReceipts = new();
    private ICaptureRun? _run;
    private string? _runSessionId, _startingSessionId, _stopReason;
    private CancellationTokenSource? _stopBeforeStart;
    private string? _roomCode = options.RoomCode?.Trim().ToUpperInvariant();
    private string? _lanNetwork;
    private bool _lanAnnounced;
    private int _reconnectGraceMs = new Welcome(0).ReconnectGraceMs;
    private long _connection;
    /// <summary>Done sent for rounds whose files have not come yet: sent again after a reconnect.</summary>
    private readonly Dictionary<string, Done> _unconfirmed = [];

    /// <summary>Human-readable progress for a UI or console.</summary>
    public event Action<string>? Log;

    /// <summary>
    /// A round starts after this countdown (ms). The capture starts with the countdown, so the video holds the moment play
    /// begins (round_start_frame). The token is cancelled if the round is stopped before the countdown ends.
    /// </summary>
    public event Action<int, CancellationToken>? Countdown;

    /// <summary>Raised with the room's latest state, or null once out of it (left, closed by the host).</summary>
    public event Action<RoomUpdate?>? RoomChanged;

    /// <summary>
    /// The orchestrator's game asks for its traffic to be recorded (spec v3 §2.3); the owner then sets <see cref="Packets"/>
    /// (odx-app: the elevated pktmon helper).
    /// </summary>
    public event Action<PacketCaptureSpec>? PacketCaptureWanted;

    /// <summary>Records each round's game traffic into packets.pcapng; null records none.</summary>
    public IPacketCapture? Packets { get; set; }

    /// <summary>Raised when the orchestrator refuses a room request (unknown invite code, not the host…).</summary>
    public event Action<string>? RoomError;

    /// <summary>Progress of joining the room's virtual LAN; null once out of it.</summary>
    public event Action<LanState?>? LanChanged;

    /// <summary>A round's capture is done (session id, its folder, whether the capture worked).</summary>
    public event Action<string, string, bool>? RoundKept;

    /// <summary>Whether this player is ready for the next round; the orchestrator clears it when play starts.</summary>
    public bool IsReady { get; private set; }

    /// <summary>The latest clock check (null until the first one after Welcome completes).</summary>
    public ClockCheck? Clock { get; private set; }

    public event Action<ClockCheck>? ClockChecked;

    /// <summary>
    /// Runs <c>odx record --probe</c> on the window to record (null: the monitor) and the game's process (the profile's,
    /// for the monitor it is on), set by the owner; null (tests) probes nothing. The result is <see cref="Capture"/>: a
    /// source that gives no frames or black ones (exclusive fullscreen) keeps the player from being ready.
    /// </summary>
    public Func<string?, string?, Task<CaptureProbe?>>? ProbeCapture { get; set; }

    public CaptureProbe? Capture { get; private set; }

    public event Action<CaptureProbe?>? CaptureProbed;

    /// <summary>The room's game wants its packets recorded (spec v3 §2.3), so <see cref="Packets"/> must be there to be ready.</summary>
    public bool PacketsWanted { get; private set; }

    public RoomUpdate? Room { get; private set; }

    public bool IsHost => Room?.HostPlayerId == options.PlayerId;

    /// <summary>The capture in progress (odx-app shows its counters); null between sessions.</summary>
    public ICaptureRun? CurrentRun => _run;

    /// <summary>
    /// Runs until <paramref name="stop"/>, reconnecting every 5 s while the orchestrator is unreachable. A refused access key
    /// ends it (retrying cannot help) and is reported through <see cref="RoomError"/>.
    /// </summary>
    public async Task RunUntilStoppedAsync(CancellationToken stop)
    {
        while (!stop.IsCancellationRequested)
        {
            try
            {
                await RunAsync(stop);
                if (!stop.IsCancellationRequested) Log?.Invoke("orchestrator closed the connection");
            }
            catch (Exception e) when (e is UnauthorizedAccessException or IncompatibleException)
            {
                Log?.Invoke(e.Message);
                RoomError?.Invoke(e.Message);
                return;
            }
            catch (Exception e) when (e is WebSocketException or HttpRequestException)
            {
                Log?.Invoke($"{options.Orchestrator}: {e.Message}");
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested)
            {
                return;
            }
            catch (OperationCanceledException e)
            {
                Log?.Invoke($"{options.Orchestrator}: {e.Message}");
            }
            try
            {
                await Task.Delay(Retry, stop);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    public async Task RunAsync(CancellationToken stop)
    {
        using var socket = NewSocket(options.AccessKey);
        _socket = socket;
        var wsUri = new UriBuilder(options.Orchestrator)
        {
            Scheme = options.Orchestrator.Scheme == "https" ? "wss" : "ws",
            Path = "/agents",
        }.Uri;
        try
        {
            await socket.ConnectAsync(wsUri, stop);
        }
        catch (WebSocketException) when (socket.HttpStatusCode == System.Net.HttpStatusCode.Unauthorized)
        {
            throw new UnauthorizedAccessException(
                $"{options.Orchestrator} refused the access key (access_key in odx-capture.json)");
        }
        long connection = Interlocked.Increment(ref _connection);
        try
        {
            IsReady = false;
            _lanAnnounced = false;
            List<string> recording;
            List<Done> unconfirmed;
            lock (_gate)
            {
                recording = new[] { _runSessionId, _startingSessionId }.OfType<string>().Distinct().ToList();
                unconfirmed = [.. _unconfirmed.Values];
            }
            await SendAsync(new Hello(options.PlayerId, options.Name, BuildInfo.Version, MachineId, SignedInSteamId(),
                Recording: recording.Count > 0 ? recording : null, Protocol: AgentMessage.Version), stop);
            Log?.Invoke($"connected to {options.Orchestrator} as {options.PlayerId}");
            foreach (var done in unconfirmed) await SendAsync(done, stop);
            if (_roomCode is not null) await SendAsync(new JoinRoom(_roomCode), stop);
            else if (options.CreateRoom) await SendAsync(new CreateRoom(options.Lan), stop);

            var buffer = new byte[64 * 1024];
            while (!stop.IsCancellationRequested && socket.State == WebSocketState.Open)
            {
                string? text = await WebSocketText.ReceiveAsync(socket, buffer, stop);
                if (text is null) break;
                await HandleAsync(AgentMessage.Parse(text), stop);
            }
        }
        finally
        {
            if (stop.IsCancellationRequested) StopCapture();
            else _ = StopIfStillGoneAsync(connection);
        }
    }

    /// <summary>
    /// A lost connection does not stop the capture: the round keeps this player for the orchestrator's grace. Still not
    /// back after as long, the orchestrator has stopped it there, so the capture stops here too.
    /// </summary>
    private async Task StopIfStillGoneAsync(long connection)
    {
        await Task.Delay(Volatile.Read(ref _reconnectGraceMs));
        if (Interlocked.Read(ref _connection) != connection) return;
        lock (_gate)
        {
            if (_run is null && _startingSessionId is null) return;
            _stopReason = "disconnected";
        }
        Log?.Invoke("no orchestrator for too long: stopping the recording");
        StopCapture();
    }

    /// <summary>PING/PONG keep-alive: a connection that died without closing (Wi-Fi gone) ends within 15 s, not minutes.</summary>
    internal static ClientWebSocket NewSocket(string? accessKey)
    {
        var socket = new ClientWebSocket();
        socket.Options.CollectHttpResponseDetails = true;
        socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(5);
        socket.Options.KeepAliveTimeout = TimeSpan.FromSeconds(10);
        if (accessKey is { } key) socket.Options.SetRequestHeader("Authorization", $"Bearer {key}");
        return socket;
    }

    /// <returns>False when ready was refused: the Windows clock is off NTP or not checked yet (<see cref="Clock"/>), the
    /// capture source gives nothing usable (<see cref="Capture"/>, probed again first), or the game's packet capture was
    /// declined (<see cref="Packets"/>).</returns>
    public async Task<bool> SetReadyAsync(bool ready, CancellationToken stop)
    {
        if (ready && options.TimeServer is not null && Clock is not { Ok: true })
        {
            Log?.Invoke(Clock is { NtpOffsetMs: double off }
                ? $"not ready: the Windows clock is {off:+0;-0} ms off NTP (limit {ClockCheck.MaxOffsetMs} ms); sync it first"
                : Clock is { NtpError: { } error } ? $"not ready: NTP {options.TimeServer} did not answer ({error})"
                : "not ready: the clock check has not finished yet");
            return false;
        }
        if (ready && Capture is { Ok: false } && (await CheckCaptureAsync()) is { Ok: false } probe)
        {
            Log?.Invoke(probe.Frames == 0
                ? $"not ready: {probe.Source} gives no frames (window minimized or gone?)"
                : $"not ready: {probe.Source} is black — exclusive fullscreen cannot be recorded: set the game to borderless/windowed, or record the monitor");
            return false;
        }
        if (ready && PacketsWanted && Packets is null)
        {
            Log?.Invoke("not ready: this game's packets must be recorded (spec v3 §2.3) and the administrator prompt was declined; restart Odx Capture and allow it");
            return false;
        }
        IsReady = ready;
        await SendAsync(new Ready(ready), stop);
        Log?.Invoke(ready ? "ready" : "not ready");
        return true;
    }

    /// <summary>Probes the source that a round would record (<see cref="ProbeCapture"/>); null when there is no prober.</summary>
    public async Task<CaptureProbe?> CheckCaptureAsync()
    {
        if (ProbeCapture is not { } probe) return null;
        var result = await probe(WindowToRecord(), _profileWindow);
        Capture = result;
        Log?.Invoke(result is null ? "capture probe: could not run odx record"
            : result.Ok ? $"capture probe: {result.Source} {result.Width}x{result.Height}{(result.WindowStyle is { } style ? $" ({style})" : "")} ok"
            : result.Frames == 0 ? $"capture probe: {result.Source} gives no frames" : $"capture probe: {result.Source} is black");
        CaptureProbed?.Invoke(result);
        return result;
    }

    /// <summary>
    /// Measures the Windows clock against NTP (<see cref="AgentOptions.TimeServer"/>) and the orchestrator's time service,
    /// a few seconds; with <see cref="AgentOptions.AutoReady"/> it then readies the player it was waiting for.
    /// </summary>
    public async Task<ClockCheck> CheckClockAsync(CancellationToken stop)
    {
        var check = await Task.Run(() => ClockCheck.Measure(options.TimeServer, _timeServer), stop);
        if (_serverClock is { } sc) check = check with { OrchestratorNtpOffsetMs = sc.OffsetMs, OrchestratorNtpMeasuredAtUtcMs = sc.MeasuredAtUtcMs };
        Clock = check;
        Log?.Invoke($"clock: NTP {(check.NtpServer is null ? "not checked" : check.NtpOffsetMs is double n ? $"{n:+0.0;-0.0} ms" : check.NtpError)}; " +
                    $"time service {(check.ReferenceOffsetMs is double r ? $"{r:+0.0;-0.0} ms" : check.ReferenceError ?? "not checked")}" +
                    (check.OrchestratorNtpOffsetMs is double o ? $"; orchestrator vs NTP {o:+0.0;-0.0} ms" : "; orchestrator has not measured NTP"));
        ClockChecked?.Invoke(check);
        if (options.AutoReady && !IsHost && !IsReady && Room is { CurrentSession: null }) await SetReadyAsync(true, stop);
        return check;
    }

    /// <summary>
    /// Host control (in place of a game server plugin): starts a round, with a countdown on every member, when none is
    /// running, otherwise ends it.
    /// </summary>
    public Task ToggleRoundAsync(int countdownMs, CancellationToken stop) => SendAsync(new ToggleRound(countdownMs), stop);

    public async Task LeaveRoomAsync(CancellationToken stop)
    {
        await SendAsync(new LeaveRoom(), stop);
        LeftRoom();
    }

    /// <summary>Host only: closes the room for every member.</summary>
    public async Task CloseRoomAsync(CancellationToken stop)
    {
        await SendAsync(new CloseRoom(), stop);
        LeftRoom();
    }

    private void LeftRoom()
    {
        _roomCode = null;
        Room = null;
        IsReady = false;
        LeaveLan();
        RoomChanged?.Invoke(null);
    }

    /// <summary>
    /// Joins the room's ZeroTier network through this machine's ZeroTier service, has the orchestrator authorize this node
    /// and waits for an address. Called again after ZeroTier has been installed.
    /// </summary>
    public async Task JoinLanAsync(CancellationToken stop)
    {
        if (Room?.LanNetworkId is not { } network) return;
        _lanNetwork = network;
        if (ZeroTierApi.LocalToken() is not { } token)
        {
            LanChanged?.Invoke(new LanState(network, "zerotier_missing"));
            return;
        }
        LanChanged?.Invoke(new LanState(network, "joining"));
        using var zeroTier = new ZeroTierApi(ZeroTierApi.Local, token);
        try
        {
            string node = await zeroTier.NodeIdAsync(stop);
            await zeroTier.JoinAsync(network, stop);
            await SendAsync(new LanMember(node), stop);
            for (int i = 0; i < 60 && _lanNetwork == network; i++)
            {
                var (status, addresses) = await zeroTier.NetworkAsync(network, stop);
                if (status == "OK" && addresses.Count > 0)
                {
                    LanChanged?.Invoke(new LanState(network, "ok", addresses[0]));
                    Log?.Invoke($"LAN {addresses[0]} (ZeroTier {network})");
                    return;
                }
                await Task.Delay(1000, stop);
            }
            if (_lanNetwork == network) LanChanged?.Invoke(new LanState(network, "error", "no address from the room's LAN after 60 s"));
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
        {
            LanChanged?.Invoke(new LanState(network, e is HttpRequestException ? "zerotier_missing" : "error", e.Message));
        }
    }

    private void LeaveLan()
    {
        if (_lanNetwork is not { } network) return;
        _lanNetwork = null;
        if (ZeroTierApi.LocalToken() is { } token)
            _ = Task.Run(async () =>
            {
                using var zeroTier = new ZeroTierApi(ZeroTierApi.Local, token);
                try
                {
                    await zeroTier.LeaveAsync(network, default);
                }
                catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
                {
                }
            });
        LanChanged?.Invoke(null);
    }

    private static string MachineId => MachineIdentity.Get();

    /// <summary>
    /// The Steam account signed in on this machine as a SteamID64, from the Steam client's registry entry (the account id
    /// of an individual account plus 76561197960265728); null when Steam is not running or not on Windows.
    /// </summary>
    private static string? SignedInSteamId()
    {
        if (!OperatingSystem.IsWindows()) return null;
        using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam\ActiveProcess");
        return key?.GetValue("ActiveUser") is int accountId and not 0
            ? (76561197960265728UL + (uint)accountId).ToString(System.Globalization.CultureInfo.InvariantCulture)
            : null;
    }

    /// <remarks>
    /// Runs on the receive loop, and the keep-alive's PONGs are only read while a receive is pending: anything that can take
    /// seconds (clock checks, hashing a round's video) goes to its own task, or the connection is dropped as dead.
    /// </remarks>
    private async Task HandleAsync(AgentMessage message, CancellationToken stop)
    {
        switch (message)
        {
            case Incompatible i:
                throw new IncompatibleException(i);
            case Welcome w:
                _timeServer = $"{options.Orchestrator.Host}:{w.TimePort}";
                _serverClock = w.ServerClock;
                Volatile.Write(ref _reconnectGraceMs, w.ReconnectGraceMs);
                _ = Task.Run(() => CheckClockAsync(stop), stop);
                if (w.GameProfileJson is { } welcomeProfile)
                {
                    var profile = GameProfile.Parse(JsonNode.Parse(welcomeProfile)!.AsObject(), "orchestrator");
                    _profileWindow = profile.WindowProcess;
                    if (profile.Network is { } network)
                    {
                        PacketsWanted = true;
                        PacketCaptureWanted?.Invoke(network);
                    }
                }
                _ = Task.Run(CheckCaptureAsync, stop);
                break;
            case RoomUpdate r:
                bool joined = Room?.Code != r.Code;
                Room = r;
                _roomCode = r.Code;
                var me = r.Members.FirstOrDefault(m => m.PlayerId == options.PlayerId);
                IsReady = me?.Ready == true;
                if (joined) Log?.Invoke($"in room {r.Code}{(IsHost ? " (host)" : "")}");
                RoomChanged?.Invoke(r);
                // Once per connection: the orchestrator authorizes this machine's node for the connection that says it.
                if (r.LanNetworkId is not null && (r.LanNetworkId != _lanNetwork || !_lanAnnounced))
                {
                    _lanNetwork = r.LanNetworkId;  // before the next update arrives, so it is joined once
                    _lanAnnounced = true;
                    _ = Task.Run(() => JoinLanAsync(stop), stop);
                }
                if (options.AutoReady && !IsHost && me is { Ready: false, State: "idle" } && r.CurrentSession is null)
                    await SetReadyAsync(true, stop);
                break;
            case GameEvent g:
                _eventReceipts[g.EventId] = (DateTime.UtcNow - DateTime.UnixEpoch).TotalMilliseconds;
                break;
            case RoundFiles files:
                lock (_gate) _unconfirmed.Remove(files.SessionId);
                _ = Task.Run(() => KeepRoundFiles(files), CancellationToken.None);
                break;
            case RoomError e:
                Log?.Invoke(e.Message);
                RoomError?.Invoke(e.Message);
                break;
            case RoomClosed c:
                Log?.Invoke($"room {c.Code} was closed by its host");
                LeftRoom();
                break;
            case Arm a:
                _armed = a;
                var armedProfile = GameProfile.Parse(JsonNode.Parse(a.GameProfileJson)!.AsObject(), "orchestrator");
                _profileWindow = armedProfile.WindowProcess;
                _armedNetwork = armedProfile.Network;
                _profilePath = Path.Combine(Path.GetTempPath(), $"odx-agent-{options.PlayerId}-{a.SessionId}.json");
                await File.WriteAllTextAsync(_profilePath, a.GameProfileJson, stop);
                await SendAsync(new AgentState("armed", a.SessionId), stop);
                Log?.Invoke($"armed for {a.SessionId}");
                break;
            case Start s when _armed?.SessionId == s.SessionId:
                lock (_gate)
                {
                    _stopReason = null;
                    _startingSessionId = s.SessionId;
                    _stopBeforeStart = CancellationTokenSource.CreateLinkedTokenSource(stop);
                }
                _ = Task.Run(() => RecordAsync(s, stop), stop);
                break;
            case Stop s when s.SessionId == _runSessionId || s.SessionId == _startingSessionId:
                Log?.Invoke($"stop {s.SessionId} ({s.Reason})");
                lock (_gate) _stopReason = s.Reason;
                StopCapture();
                break;
        }
    }

    /// <summary>
    /// The first open window of: the player's choice, the game's window from the profile; else the primary monitor (the
    /// session's capture.window check then flags it). A round never fails just because a game is not open.
    /// </summary>
    private string? WindowToRecord()
    {
        foreach (string? process in new[] { options.WindowProcess, _profileWindow }.Distinct())
        {
            if (process is null) continue;
            if (HasWindow(process)) return process;
            Log?.Invoke($"{process} is not running");
        }
        if (options.WindowProcess is not null || _profileWindow is not null) Log?.Invoke("recording the primary monitor");
        return null;
    }

    private static bool HasWindow(string process)
    {
        var running = System.Diagnostics.Process.GetProcessesByName(process);
        bool open = running.Any(p => p.MainWindowHandle != IntPtr.Zero);
        foreach (var p in running) p.Dispose();
        return open;
    }

    /// <summary>A Stop can arrive during the countdown or while the capture is starting; both end up here.</summary>
    private void StopCapture()
    {
        lock (_gate)
        {
            _stopBeforeStart?.Cancel();
            _run?.Stop();
        }
    }

    private async Task RecordAsync(Start s, CancellationToken stop)
    {
        CancellationToken stopped;
        lock (_gate) stopped = _stopBeforeStart!.Token;
        if (s.CountdownMs > 0) Countdown?.Invoke(s.CountdownMs, stopped);
        string? etl = await StartPacketsAsync(s.SessionId, stop);
        ICaptureRun run;
        try
        {
            run = startCapture(new CaptureOptions(options.OutputRoot, WindowToRecord(), _armed!.Fps, _profilePath, _timeServer,
                NtpIntervalS: 1, DirName: s.SessionId, Player: new PlayerIdentity(options.PlayerId, options.Name)));
        }
        catch (Exception e)
        {
            if (etl is not null) await StopPacketsAsync(etl, null, stop);
            await EndWithoutCaptureAsync(s.SessionId, e.Message, stop);
            return;
        }
        lock (_gate)
        {
            _run = run;
            _runSessionId = s.SessionId;
            _startingSessionId = null;
            if (stopped.IsCancellationRequested) run.Stop();
        }
        await SendAsync(new AgentState("recording", s.SessionId), stop);
        Log?.Invoke($"recording {s.SessionId} into {run.SessionDir}");
        if (Clock is { } clock)
        {
            // Layer 1 evidence for player.json; the capture's Tidy moves it to raw/ with the other logs.
            try
            {
                clock.Save(Path.Combine(run.SessionDir, ClockCheck.FileName));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                Log?.Invoke($"cannot write {ClockCheck.FileName}: {e.Message}");
            }
        }

        var result = await run.Completion;
        if (etl is not null) await StopPacketsAsync(etl, result.SessionDir, stop);
        if (Volatile.Read(ref _stopReason) == Stop.Cancelled)
        {
            lock (_gate)
            {
                _run = null;
                _runSessionId = null;
            }
            string aside = $"{result.SessionDir}.cancelled-{DateTime.Now:yyyyMMdd-HHmmss}";
            try
            {
                Directory.Move(result.SessionDir, aside);
            }
            catch (IOException e)
            {
                Log?.Invoke($"cannot move the cancelled recording aside: {e.Message}");
            }
            await EndWithoutCaptureAsync(s.SessionId, $"round cancelled, recording kept locally only ({aside})", stop, cancelled: true);
            return;
        }
        bool ok = result.CaptureOk;
        await SendDoneAsync(new Done(s.SessionId, ok, ok ? null : "capture failed"), stop);
        await SendAsync(new AgentState("idle"), stop);
        Log?.Invoke($"{s.SessionId} done: {(ok ? $"kept in {result.SessionDir}" : "capture failed")}");
        if (result.Recording?.Audio is { } audio && (audio.Source != AudioStatus.FromProcess || audio.SilentRatio > 0.99 || audio.Error is not null))
            Log?.Invoke($"{s.SessionId} audio: {audio.Source}{(audio.FallbackReason is { } f ? $" ({f})" : "")}" +
                        $"{(audio.SilentRatio is double r ? $", {r:P0} silent" : "")}{(audio.Error is { } e ? $", {e}" : "")}");
        RoundKept?.Invoke(s.SessionId, result.SessionDir, ok);
        lock (_gate)
        {
            _run = null;
            _runSessionId = null;
        }
    }

    /// <summary>The .etl pktmon writes to while recording, or null when this round captures no packets.</summary>
    private async Task<string?> StartPacketsAsync(string sessionId, CancellationToken stop)
    {
        if (Packets is not { } packets || _armedNetwork is not { } network) return null;
        string etl = Path.Combine(Path.GetTempPath(), $"odx-packets-{options.PlayerId}-{sessionId}.etl");
        try
        {
            await packets.StartAsync(network.Ports, etl, stop);
            return etl;
        }
        catch (Exception e) when (e is InvalidOperationException or ArgumentException or IOException)
        {
            Log?.Invoke($"packet capture not started: {e.Message}");
            return null;
        }
    }

    /// <param name="sessionDir">Where packets.pcapng goes (kept with the capture); null discards the capture.</param>
    private async Task StopPacketsAsync(string etl, string? sessionDir, CancellationToken stop)
    {
        string pcapng = sessionDir is null
            ? Path.Combine(Path.GetTempPath(), Path.GetFileNameWithoutExtension(etl) + ".pcapng")
            : CaptureFolder.Raw(sessionDir, PacketCaptureFiles.Pcapng);
        try
        {
            await Packets!.StopAsync(etl, pcapng, stop);
            if (sessionDir is null) File.Delete(pcapng);
            else PacketCaptureFiles.WriteLocalAddresses(CaptureFolder.RawFolder(sessionDir));
        }
        catch (Exception e) when (e is InvalidOperationException or ArgumentException or IOException)
        {
            Log?.Invoke($"packet capture: {e.Message}");
        }
    }

    /// <param name="cancelled">A cancelled round: the orchestrator waits for nobody, so the Done is not sent again.</param>
    private async Task EndWithoutCaptureAsync(string sessionId, string reason, CancellationToken stop, bool cancelled = false)
    {
        lock (_gate) _startingSessionId = null;
        var done = new Done(sessionId, false, reason);
        await (cancelled ? SendAsync(done, stop) : SendDoneAsync(done, stop));
        await SendAsync(new AgentState("idle"), stop);
        Log?.Invoke($"{sessionId}: {reason}");
    }

    private Task SendDoneAsync(Done done, CancellationToken stop)
    {
        lock (_gate) _unconfirmed[done.SessionId] = done;
        return SendAsync(done, stop);
    }

    /// <summary>Checks the time service answers before any round; the capture itself samples it at start and every 15 s.</summary>
    private void KeepRoundFiles(RoundFiles files)
    {
        string kept = Path.Combine(options.OutputRoot, files.SessionId);
        try
        {
            if (!Directory.Exists(kept)) return;
            string raw = CaptureFolder.RawFolder(kept);
            files.WriteTo(raw);
            long last = PlayerTimeline.WriteRoundEvents(kept, RoundRecord.Load(raw), _eventReceipts);
            foreach (long id in _eventReceipts.Keys.Where(id => id <= last)) _eventReceipts.TryRemove(id, out _);
            Manifest.Write(kept);
        }
        catch (Exception e) when (e is IOException or JsonException or FormatException or InvalidDataException)
        {
            Log?.Invoke($"{files.SessionId}: cannot write the round's files: {e.Message}");
        }
    }

    private async Task SendAsync(AgentMessage message, CancellationToken stop)
    {
        if (_socket is not { State: WebSocketState.Open } socket) return;
        await _sendLock.WaitAsync(stop);
        try
        {
            await socket.SendAsync(Encoding.UTF8.GetBytes(message.ToJson()), WebSocketMessageType.Text, true, stop);
        }
        finally
        {
            _sendLock.Release();
        }
    }
}
