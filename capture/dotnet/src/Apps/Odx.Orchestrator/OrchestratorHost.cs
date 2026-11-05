using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Odx.Core;
using Odx.Protocol;

namespace Odx.Orchestrator;

/// <summary>
/// HTTP API (AgentClient, GameServerAgent and a game server plugin are the clients):
///   GET  /agents               WebSocket, one per agent (Hello → Welcome, rooms, Arm/Start/Stop ↔ state/done)
///   POST /rooms[?server={id}]  open a room without a host (game server plugin) → {"code": …}; with server, that game
///                              server agent records its rounds
///   POST /rooms/{code}/events  one GameEvents JSON object for that room
///   GET  /rooms/{code}         the room's members, round and sessions
///   GET  /status               every room
/// Captures stay with their participants; nothing is uploaded here.
/// plus the NTP responder on UDP <see cref="OrchestratorOptions.TimePort"/>. With an access key, every HTTP and WebSocket
/// request needs "Authorization: Bearer &lt;key&gt;" (401 otherwise); NTP stays open, it reveals only the time.
/// </summary>
public static class OrchestratorHost
{
    public static WebApplication Build(OrchestratorOptions options, string bindAddress = "0.0.0.0")
    {
        var udp = NtpResponder.Bind(options.TimePort);
        int timePort = ((IPEndPoint)udp.Client.LocalEndPoint!).Port;

        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls($"http://{bindAddress}:{options.HttpPort}");
        builder.Services.AddSingleton(sp =>
        {
            var coordinator = new MatchCoordinator(options, timePort, sp.GetRequiredService<ILogger<MatchCoordinator>>());
            coordinator.Restore();
            return coordinator;
        });
        var app = builder.Build();

        app.Lifetime.ApplicationStarted.Register(() => _ = NtpResponder.RunAsync(udp, app.Lifetime.ApplicationStopping));
        if ((options.TimeServer ?? CaptureConfig.Load().TimeServer) is { } timeServer)
            app.Lifetime.ApplicationStarted.Register(() => _ = Task.Run(() =>
                app.Services.GetRequiredService<MatchCoordinator>().MeasureClockAsync(timeServer, app.Lifetime.ApplicationStopping)));
        app.Lifetime.ApplicationStarted.Register(() => _ = Task.Run(() =>
            app.Services.GetRequiredService<MatchCoordinator>().SweepRoomsAsync(app.Lifetime.ApplicationStopping)));
        app.Lifetime.ApplicationStopped.Register(udp.Dispose);

        if (options.AccessKey is { Length: > 0 } key)
        {
            byte[] expected = Encoding.UTF8.GetBytes("Bearer " + key);
            app.Use(async (context, next) =>
            {
                byte[] presented = Encoding.UTF8.GetBytes(context.Request.Headers.Authorization.ToString());
                if (!CryptographicOperations.FixedTimeEquals(presented, expected))
                {
                    context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                    return;
                }
                await next();
            });
        }
        // PING/PONG both ways: a connection that died without closing (Wi-Fi gone) is noticed within 15 s, not minutes.
        app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(5), KeepAliveTimeout = TimeSpan.FromSeconds(10) });
        app.Map("/agents", async (HttpContext context, MatchCoordinator coordinator) =>
        {
            if (!context.WebSockets.IsWebSocketRequest)
            {
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                return;
            }
            using var socket = await context.WebSockets.AcceptWebSocketAsync();
            // Shutting down ends every agent connection at once, rather than waiting the 30 s host shutdown timeout for them.
            using var ended = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted, app.Lifetime.ApplicationStopping);
            await coordinator.HandleAgentAsync(socket, ended.Token);
        });
        app.MapPost("/rooms", (string? server, MatchCoordinator coordinator) =>
            Results.Text(new JsonObject { ["code"] = coordinator.OpenRoom(server) }.ToJsonString(), "application/json"));
        app.MapGet("/rooms/{code}", (string code, MatchCoordinator coordinator) =>
            coordinator.RoomStatus(code) is { } room ? Results.Text(room.ToJsonString(AgentMessage.Json), "application/json") : Results.NotFound());
        app.MapPost("/rooms/{code}/events", async (string code, HttpRequest request, MatchCoordinator coordinator) =>
        {
            try
            {
                if (await JsonNode.ParseAsync(request.Body) is not JsonObject e) return Results.BadRequest("expected a JSON object");
                return await coordinator.PostEventAsync(code, e) is { } stamped
                    ? Results.Text(stamped.ToJsonString(AgentMessage.Json), "application/json")
                    : Results.NotFound($"no room {code}");
            }
            catch (Exception e) when (e is System.Text.Json.JsonException or FormatException or InvalidOperationException)
            {
                return Results.BadRequest(e.Message);
            }
        });
        app.MapGet("/status", (MatchCoordinator coordinator) => Results.Text(coordinator.Status().ToJsonString(AgentMessage.Json), "application/json"));
        return app;
    }
}
