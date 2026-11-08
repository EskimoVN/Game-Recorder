using System.Net.WebSockets;
using System.Text;
using Microsoft.Extensions.Logging;
using Odx.Core;
using Odx.Protocol;

namespace Odx.Agent;

/// <param name="WorkDir">Each round is kept in &lt;WorkDir&gt;/&lt;session&gt;/ (server/ and the round's files).</param>
/// <param name="AccessKey">The orchestrator's access key; null when it has none.</param>
/// <param name="Id">Names this game server to the orchestrator; rooms opened for it (POST /rooms?server=…) are its own.</param>
public sealed record GameServerAgentOptions(Uri Orchestrator, string WorkDir, string? AccessKey = null,
    string Name = "game server", string Id = "game-server");

/// <summary>A game's own recording of the server's side of a round (CS2: Odx.Games.Cs2.Cs2Server).</summary>
public interface IGameServerRecorder
{
    /// <summary>Starts recording into &lt;<paramref name="dir"/>&gt;/server/.</summary>
    Task StartAsync(string sessionId, string dir);

    /// <param name="keep">False for a cancelled round: what it recorded goes.</param>
    Task StopAsync(string sessionId, string dir, bool keep);
}

/// <summary>
/// The game server machine's agent (odx game-server). It connects to the orchestrator like a player's agent but joins no room:
/// every round's Start records the server's side (<see cref="IGameServerRecorder"/>) and samples this machine's clock against
/// the orchestrator's time service; Stop keeps both and reports Done.
/// The round's files (<see cref="RoundFiles"/>) join them once everyone is done. A cancelled round is dropped.
/// </summary>
public sealed class GameServerAgent(GameServerAgentOptions options, IGameServerRecorder recorder, ILogger log)
{
    /// <summary>This machine's clock against the orchestrator's during a round, in &lt;round&gt;/server/.</summary>
    public const string ClockSamplesFile = "ntp_samples.csv";

    private static readonly TimeSpan SampleEvery = TimeSpan.FromSeconds(1), NtpTimeout = TimeSpan.FromSeconds(2),
        Retry = TimeSpan.FromSeconds(5);

    private sealed record OpenRound(string Dir, CancellationTokenSource StopSampling, Task Sampling);

    private readonly Dictionary<string, OpenRound> _rounds = [];
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private ClientWebSocket? _socket;
    private string? _timeServer;
    private int _reconnectGraceMs = new Welcome(0).ReconnectGraceMs;
    private long _connection;
    /// <summary>Done sent for rounds whose files have not come yet: sent again after a reconnect.</summary>
    private readonly Dictionary<string, Done> _unconfirmed = [];

    /// <summary>Runs until <paramref name="stop"/>, reconnecting every 5 s; a refused access key ends it.</summary>
    public async Task RunUntilStoppedAsync(CancellationToken stop)
    {
        while (!stop.IsCancellationRequested)
        {
            try
            {
                await RunAsync(stop);
                if (!stop.IsCancellationRequested) log.LogWarning("orchestrator closed the connection");
            }
            catch (Exception e) when (e is UnauthorizedAccessException or IncompatibleException)
            {
                log.LogError("{Error}", e.Message);
                return;
            }
            catch (Exception e) when (e is WebSocketException or HttpRequestException)
            {
                log.LogWarning("{Orchestrator}: {Error}", options.Orchestrator, e.Message);
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested)
            {
                return;
            }
            catch (OperationCanceledException e)
            {
                log.LogWarning("{Orchestrator}: {Error}", options.Orchestrator, e.Message);
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
        using var socket = AgentClient.NewSocket(options.AccessKey);
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
            throw new UnauthorizedAccessException($"{options.Orchestrator} refused the access key");
        }
        _socket = socket;
        long connection = Interlocked.Increment(ref _connection);
        try
        {
            List<string> recording;
            lock (_rounds) recording = [.. _rounds.Keys];
            List<Done> unconfirmed;
            lock (_unconfirmed) unconfirmed = [.. _unconfirmed.Values];
            await SendAsync(new Hello(options.Id, options.Name, BuildInfo.Version, Role: Hello.GameServerRole,
                Recording: recording.Count > 0 ? recording : null, Protocol: AgentMessage.Version), stop);
            log.LogInformation("connected to {Orchestrator}", options.Orchestrator);
            foreach (var done in unconfirmed) await SendAsync(done, stop);
            await ReceiveAsync(socket, stop);
        }
        finally
        {
            if (!stop.IsCancellationRequested) _ = StopIfStillGoneAsync(connection, stop);
        }
    }

    /// <summary>Still no orchestrator after its grace: it has given up on this server's rounds, which stop and are kept.</summary>
    private async Task StopIfStillGoneAsync(long connection, CancellationToken stop)
    {
        await Task.Delay(Volatile.Read(ref _reconnectGraceMs), CancellationToken.None);
        if (Interlocked.Read(ref _connection) != connection) return;
        List<string> open;
        lock (_rounds) open = [.. _rounds.Keys];
        foreach (string sid in open)
        {
            log.LogWarning("{Session}: no orchestrator for too long, stopping", sid);
            await StopAsync(new Stop(sid, "disconnected"), stop);
        }
    }

    private async Task ReceiveAsync(ClientWebSocket socket, CancellationToken stop)
    {
        var buffer = new byte[64 * 1024];
        while (!stop.IsCancellationRequested && socket.State == WebSocketState.Open)
        {
            if (await WebSocketText.ReceiveAsync(socket, buffer, stop) is not { } text) break;
            switch (AgentMessage.Parse(text))
            {
                case Incompatible i:
                    throw new IncompatibleException(i);
                case Welcome w:
                    _timeServer = $"{options.Orchestrator.Host}:{w.TimePort}";
                    Volatile.Write(ref _reconnectGraceMs, w.ReconnectGraceMs);
                    break;
                case RoundFiles files:
                    lock (_unconfirmed) _unconfirmed.Remove(files.SessionId);
                    string kept = Path.Combine(options.WorkDir, files.SessionId);
                    try
                    {
                        if (Directory.Exists(kept)) files.WriteTo(kept);
                    }
                    catch (IOException e)
                    {
                        log.LogError("{Session}: cannot write the round's files: {Error}", files.SessionId, e.Message);
                    }
                    break;
                case Start s:
                    await StartAsync(s.SessionId);
                    break;
                case Stop s:
                    // Off the receive loop: stopping takes seconds, and the next round may start meanwhile.
                    _ = Task.Run(() => StopAsync(s, stop), CancellationToken.None);
                    break;
            }
        }
    }

    private async Task StartAsync(string sessionId)
    {
        string dir = Path.Combine(options.WorkDir, sessionId);
        if (Directory.Exists(dir))
        {
            string aside = $"{dir}.replaced-{DateTime.Now:yyyyMMdd-HHmmss}";
            try
            {
                Directory.Move(dir, aside);
                log.LogInformation("{Session}: the earlier take moved to {Aside}", sessionId, aside);
            }
            catch (IOException e)
            {
                log.LogWarning("{Session}: cannot move {Dir} aside: {Error}", sessionId, dir, e.Message);
            }
        }
        Directory.CreateDirectory(Path.Combine(dir, "server"));
        // Sampling first: starting SourceTV waits up to 3 s for its demo, and a short round may end meanwhile.
        var stopSampling = new CancellationTokenSource();
        var sampling = Task.Run(() => SampleClockAsync(Path.Combine(dir, "server", ClockSamplesFile), stopSampling.Token));
        lock (_rounds) _rounds[sessionId] = new OpenRound(dir, stopSampling, sampling);
        await recorder.StartAsync(sessionId, dir);
        log.LogInformation("{Session}: recording", sessionId);
    }

    /// <summary>Every second until <paramref name="stop"/>, and once more then: at least the round's start and end.</summary>
    private async Task SampleClockAsync(string path, CancellationToken stop)
    {
        using var writer = new NtpSampleWriter(new StreamWriter(path));
        while (true)
        {
            try
            {
                if (_timeServer is { } server)
                {
                    writer.Write(SntpClient.Query(server, NtpTimeout));
                    writer.Flush();
                }
            }
            catch (Exception e) when (e is System.Net.Sockets.SocketException or InvalidDataException)
            {
                log.LogWarning("time service {Server}: {Error}", _timeServer, e.Message);
            }
            if (stop.IsCancellationRequested) return;
            try
            {
                await Task.Delay(SampleEvery, stop);
            }
            catch (OperationCanceledException)
            {
            }
        }
    }

    private async Task StopAsync(Stop s, CancellationToken stop)
    {
        OpenRound? round;
        lock (_rounds) _rounds.Remove(s.SessionId, out round);
        if (round is null)
        {
            if (s.Reason != Stop.Cancelled) await SendAsync(new Done(s.SessionId, false, "the game server agent did not record this round"), stop);
            return;
        }
        await round.StopSampling.CancelAsync();
        await round.Sampling;
        bool keep = s.Reason != Stop.Cancelled;
        await recorder.StopAsync(s.SessionId, round.Dir, keep);
        if (!keep)
        {
            Directory.Delete(round.Dir, true);
            return;
        }
        var done = new Done(s.SessionId, true);
        lock (_unconfirmed) _unconfirmed[s.SessionId] = done;
        await SendAsync(done, stop);
        log.LogInformation("{Session}: kept in {Dir}", s.SessionId, round.Dir);
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
