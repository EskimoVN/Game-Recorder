using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Odx.Capture;
using Odx.Core;
using Odx.Merge.Proto;
using Odx.Protocol;

namespace Odx.Merge;

/// <summary>
/// Limits of the session gate. The sync limits are proposals (spec v3 §4: "more prototyping is needed to decide what a good
/// tolerance is"); the floors are spec v3 §2 (720p/30 floor) and the client's sample delivery (10 s, camera on 90%).
/// </summary>
public sealed record GateTolerances
{
    public static readonly GateTolerances Default = new();

    /// <summary>The clock offset is known to ±RTT/2; above this the player's timestamps are not trusted.</summary>
    public double MaxRttMs { get; init; } = 100;
    /// <summary>Spec v3 §4 layer 1: every machine's Windows clock (players and the orchestrator) within this of NTP.</summary>
    public double MaxSystemClockOffsetMs { get; init; } = ClockCheck.MaxOffsetMs;
    /// <summary>First frame may come this long after round_start (capture start-up takes ~0.7 s warm, ~3.5 s cold).</summary>
    public double StartGraceMs { get; init; } = 3000;
    /// <summary>Last frame may end this long before the player's stop (round end or permanent death).</summary>
    public double EndGraceMs { get; init; } = 1000;
    /// <summary>K of the "K of N player streams valid" rule; null: N for up to 3 players, else N−1 (plan Q6 proposal).</summary>
    public int? MinValidPlayers { get; init; }
    public double MinFps { get; init; } = 30;
    public int MinWidth { get; init; } = 1280;
    public int MinHeight { get; init; } = 720;
    public double MinDurationMs { get; init; } = 10_000;
    /// <summary>Share of frames with a camera, when the capture has camera data at all (C2W is "where extractable").</summary>
    public double MinCameraCoverage { get; init; } = 0.9;
}

/// <param name="Required">A failed required check makes the player stream invalid; the others are reported only.</param>
public sealed record QaCheck(string Id, string Check, bool Pass, string Detail, bool Required = true);

public sealed record PlayerReport(string PlayerId, bool Pass, List<Issue> Issues);

public sealed record SessionReport(string SessionId, bool Pass, int ValidPlayers, int RequiredPlayers, List<Issue> Issues,
    List<PlayerReport> Players);

/// <summary>
/// Turns a staged round (round.json, server_events.jsonl, game_profile.json, players/&lt;id&gt;/ with each agent's kept
/// capture, server/ with the game server's own capture when there is one) into the spec v3 tree and checks it:
/// <code>
/// &lt;vendor&gt;/&lt;mm-dd-yyyy&gt;/&lt;game&gt;/&lt;session-id&gt;/
///   session.json  validation.json  schema.bin.gz  server/events.jsonl  [server/server_state.bin packets.bin packets.txt]
///   players/&lt;id&gt;/ player.json frames.csv frames.bin video.mp4 events.jsonl key_bindings.json [packets.bin packets.txt]
/// _work/&lt;session-id&gt;/ players/&lt;id&gt;/ (v2 session.json, raw logs, packets.pcapng) server/ (the game server's own recording)
/// </code>
/// Only the vendor tree is delivered (spec v3 §1, key_bindings.json per SOW §2); <see cref="DeliveryFiles"/> checks that
/// nothing else is in it. Each player's stream is cut to start at round_start (players record from the countdown) and,
/// when a stop gave up its last frames on some machine, to end where every player still captured (<see cref="CommonEnd"/>).
/// Every frame gets a server UTC from the player's samples against the orchestrator's time service, so player
/// events.jsonl carry the time and frame at which each server event happened in that player's video.
/// </summary>
public static class MultiplayerSession
{
    private static readonly string[] CaptureFiles =
        ["session.json", "recording.json", "video_frames.csv", "input_events.csv", "clock_samples.csv", "ntp_samples.csv"];

    /// <summary>Version of the client's multiplayer spec this tree follows (document header of spec v3).</summary>
    public const string SpecVersion = "3.1";

    /// <summary>Next to the vendor tree under the output root: each session's working files, kept but not delivered.</summary>
    public const string WorkDir = "_work";

    /// <param name="games">What odx merge knows of each game: the round's game title picks its server reader, the profile's
    /// network.protocol its packet decoder.</param>
    public static (string Dir, SessionReport Report) Assemble(string stagingDir, string outRoot, string vendor,
        GateTolerances? tolerances = null, GameModules? games = null)
    {
        var tol = tolerances ?? GateTolerances.Default;
        games ??= GameModules.None;
        var round = RoundRecord.Load(stagingDir);
        var events = ReadEvents(Path.Combine(stagingDir, RoundRecord.EventsFileName));
        string profilePath = Path.Combine(stagingDir, RoundRecord.ProfileFileName);
        var profile = File.Exists(profilePath) ? JsonNode.Parse(File.ReadAllText(profilePath))?.AsObject() : null;
        string dir = Path.Combine(outRoot, vendor, DateTime.UtcNow.ToString("MM-dd-yyyy", CultureInfo.InvariantCulture),
            Slug(round.Game), round.SessionId);
        string work = Path.Combine(outRoot, WorkDir, round.SessionId);
        Directory.CreateDirectory(Path.Combine(dir, "server"));
        if (Directory.Exists(work)) Directory.Delete(work, true);
        string stagedServer = Path.Combine(stagingDir, "server");
        JsonObject? serverInfo = null;
        string? serverError = null;
        List<string> serverProblems = [];
        IReadOnlyDictionary<string, IServerCameraTrack> cameras = new Dictionary<string, IServerCameraTrack>();
        if (games.Servers.FirstOrDefault(g => g.Game == round.Game) is { } game && game.HasServerSide(stagedServer))
        {
            // The game server's recording is read by a third-party parser: a file it cannot read must not cost the
            // players' streams their session.
            try
            {
                var server = game.WriteServer(stagedServer, Path.Combine(dir, "server"), round);
                MoveToWork(Path.Combine(dir, "server", "capture"), Path.Combine(work, "server"));
                events = events.Concat(server.Events).OrderBy(GameEvents.UtcMs).ToList();
                serverInfo = server.Info;
                cameras = server.Cameras;
                serverProblems = server.Problems;
                if (serverProblems.Count > 0) serverInfo["problems"] = new JsonArray(serverProblems.Select(p => (JsonNode)p).ToArray());
            }
            catch (Exception e)
            {
                serverError = $"{e.GetType().Name}: {e.Message}";
            }
        }
        PlayerTimeline.WriteEvents(Path.Combine(dir, "server", "events.jsonl"), events);
        FramesBin.WriteSchema(Path.Combine(dir, FramesBin.SchemaFileName));

        double? endUtc = CommonEnd(round);
        var reports = round.Players.Select(p => AssemblePlayer(round, p, endUtc, events, profile, cameras, games, stagingDir, dir, work, tol)).ToList();
        int valid = reports.Count(r => r.Pass);
        int required = tol.MinValidPlayers ?? (round.Players.Count <= 3 ? round.Players.Count : round.Players.Count - 1);
        var issues = new List<Issue>();
        foreach (string type in new[] { GameEvents.RoundStart, GameEvents.RoundEnd })
            if (!events.Any(e => GameEvents.Type(e) == type))
                issues.Add(Issue.Error("session.events", $"no {type} event from the game server"));
        if (valid < required)
            issues.Add(Issue.Error("session.min_players", $"{valid} of {round.Players.Count} player streams valid, {required} required"));
        if (round.ServerClock is not { } serverClock)
            issues.Add(Issue.Error("session.server_clock", "the orchestrator did not measure its clock against NTP (spec v3 §4 layer 1)"));
        else if (Math.Abs(serverClock.OffsetMs) > tol.MaxSystemClockOffsetMs)
            issues.Add(Issue.Error("session.server_clock",
                $"the orchestrator's clock is {serverClock.OffsetMs:+0;-0} ms off NTP {serverClock.Server} (limit {tol.MaxSystemClockOffsetMs} ms)"));
        if (serverError is not null) issues.Add(Issue.Warning("server.capture", $"the game server's capture could not be read: {serverError}"));
        if (DeliveryFiles.Unexpected(dir) is { Count: > 0 } extra)
            issues.Add(Issue.Error("delivery.files", $"not in spec v3 §1's tree: {string.Join(", ", extra)}"));
        foreach (string problem in serverProblems) issues.Add(Issue.Warning("server.capture", problem));
        var report = new SessionReport(round.SessionId, issues.All(i => i.Severity != Issue.ErrorSeverity), valid, required, issues, reports);

        var session = new JsonObject
        {
            ["spec_version"] = SpecVersion,
            ["session_id"] = round.SessionId,
            ["match_id"] = round.MatchId,
            ["vendor_name"] = vendor,
            ["game_title"] = round.Game,
            ["game_engine"] = profile?["engine"]?.DeepClone(),
            ["coordinate_system"] = profile?["coordinate_system"]?.DeepClone(),
            ["rules"] = new JsonObject
            {
                ["start_trigger"] = round.CountdownMs > 0 ? "host (odx-app), after a countdown" : "game server",
                ["stop_trigger"] = round.EndReason?.StartsWith("player_stop:", StringComparison.Ordinal) == true ? "host (odx-app)" : "game server",
                ["recording"] = "from the countdown until the round ends, delivered from the first frame at or after round_start " +
                                "(player.json pre_roll_frames: how many were cut); a permanent death stops that player; when a " +
                                "stop gave up its last frames on a machine, every stream ends where all players still captured (end_cut)",
                ["respawn_allowed"] = profile?["rules"]?["respawn_allowed"]?.DeepClone(),
            },
            ["map"] = round.Map ?? serverInfo?["map"]?.DeepClone(),
            ["round_index"] = round.Round,
            ["countdown_ms"] = round.CountdownMs,
            ["end_reason"] = round.EndReason,
            ["created_at_utc"] = UtcText.Iso(round.StartUtcMs),
            ["ended_at_utc"] = (endUtc ?? round.EndUtcMs) is double end ? UtcText.Iso(end) : null,
            ["duration_ms"] = (endUtc ?? round.EndUtcMs) is double e2 ? e2 - round.StartUtcMs : null,
            ["end_cut"] = endUtc is double cut && round.EndUtcMs is double gameEnd
                ? new JsonObject
                {
                    ["ms"] = Math.Round(gameEnd - cut, 1),
                    ["game_round_end_utc"] = UtcText.Iso(gameEnd),
                    ["players"] = new JsonArray(round.Players.Where(p => p.StopReason != "permanent_death" && p.Verdict != CaptureReport.Failed &&
                                                                        GivenUpAt(round, p) is not null)
                        .Select(p => (JsonNode)p.PlayerId).ToArray()),
                    ["reason"] = "the stop on these machines gave up the last captured frame (the frame before stood in until " +
                                 "round_end); every stream ends at the last frame all players captured",
                }
                : null,
            ["players"] = new JsonArray(round.Players.Select(p => (JsonNode)new JsonObject
            {
                ["player_id"] = p.PlayerId,
                ["name"] = p.Name,
                ["machine_id"] = p.MachineId,
                ["steam_id"] = p.SteamId,
                ["tool_version"] = p.ToolVersion,
                ["stop_reason"] = p.StopReason,
                ["valid"] = reports.First(r => r.PlayerId == p.PlayerId).Pass,
            }).ToArray()),
            ["valid_players"] = valid,
            ["time_service_port"] = round.TimePort,
            ["server_clock"] = round.ServerClock is { } c
                ? new JsonObject
                {
                    ["ntp_server"] = c.Server,
                    ["ntp_offset_ms"] = Math.Round(c.OffsetMs, 3),
                    ["ntp_round_trip_ms"] = Math.Round(c.RttMs, 3),
                    ["measured_at_utc"] = UtcText.Iso(c.MeasuredAtUtcMs),
                }
                : null,
            ["server"] = serverInfo,
            ["files"] = new JsonObject
            {
                ["schema"] = FramesBin.SchemaFileName,
                ["server_events"] = "server/events.jsonl",
                ["server_state"] = serverInfo?["server_state"] is null ? null : $"server/{ServerSide.StateFile}",
                ["server_packets"] = serverInfo?["packets"] is null ? null : $"server/{PacketLog.BinFile}",
                ["players_root"] = "players/<player-id>/",
            },
            ["decoding"] = new JsonObject
            {
                ["schema_format"] = "gzip-compressed google.protobuf.FileDescriptorSet for package odx.capture.v1",
                ["bin_layout"] = "each .bin file is a sequence of varint length-delimited protobuf messages; the first is " +
                                 "odx.capture.v1.FileHeader, whose record_type names the type of every following message",
                ["frames.bin"] = "odx.capture.v1.FrameRecord: one per frames.csv row, full precision, plus server_time_us",
                ["packets.bin"] = "odx.capture.v1.PacketRecord: one per decoded game message (or per UDP/TCP payload when the " +
                                  "game's protocol is kept raw), with server_time_us; packets.txt lists the same records as text; " +
                                  "server/packets.bin holds the net messages the game server exchanged with each client " +
                                  "(peer_id: the client's SteamID64)",
                ["server_state.bin"] = "odx.capture.v1.ServerStateRecord: one per server tick, every player's state (matched to " +
                                       "players/<id>/player.json by steam_id) and the world entities that matter to play",
            },
            ["vendor_tool_version"] = BuildInfo.Version,
        };
        File.WriteAllText(Path.Combine(dir, "session.json"), session.ToJsonString(RoundRecord.PrettyJson));
        File.WriteAllText(Path.Combine(dir, "validation.json"), JsonSerializer.Serialize(report, RoundRecord.PrettyJson));
        return (dir, report);
    }

    /// <param name="endUtc">Where every stream of the round ends (<see cref="CommonEnd"/>); null for the game's round_end.</param>
    private static PlayerReport AssemblePlayer(RoundRecord round, RoundPlayer player, double? endUtc, List<JsonObject> events,
        JsonObject? profile, IReadOnlyDictionary<string, IServerCameraTrack> cameras, GameModules games, string stagingDir,
        string sessionDir, string workDir, GateTolerances tol)
    {
        string src = Path.Combine(stagingDir, "players", player.PlayerId);
        string work = Path.Combine(workDir, "players", player.PlayerId);
        string dst = Path.Combine(sessionDir, "players", player.PlayerId);
        Directory.CreateDirectory(dst);
        Directory.CreateDirectory(work);
        var checks = new List<QaCheck>();
        var info = new JsonObject
        {
            ["player_id"] = player.PlayerId,
            ["name"] = player.Name,
            ["is_bot"] = false,
            ["machine_id"] = player.MachineId,
            ["steam_id"] = player.SteamId,
            ["tool_version"] = player.ToolVersion,
            ["stop_reason"] = player.StopReason,
        };

        bool present = File.Exists(Path.Combine(src, "frames.csv")) && File.Exists(CaptureFolder.Raw(src, "video_frames.csv")) &&
                       File.Exists(CaptureFolder.Find(src, "session.json"));
        checks.Add(new QaCheck("player.missing", "the player's built capture is there", present,
            present ? "frames.csv, video_frames.csv, session.json" : player.Error ?? "no built capture"));
        if (!present) return Finish(dst, info, player.PlayerId, checks);

        foreach (string f in CaptureFiles.Where(f => File.Exists(CaptureFolder.Find(src, f))))
            File.Copy(CaptureFolder.Find(src, f), Path.Combine(work, f), true);
        File.Copy(Path.Combine(src, "frames.csv"), Path.Combine(dst, "frames.csv"), true);
        foreach (string f in new[] { "video.mp4", SessionBuilder.KeyBindingsFile }.Where(f => File.Exists(Path.Combine(src, f))))
            File.Copy(Path.Combine(src, f), Path.Combine(dst, f), true);

        var v2 = SessionValidator.ValidateSession(src).Where(i => i.Severity == Issue.ErrorSeverity).ToList();
        checks.Add(new QaCheck("player.v2", "spec v2 §1.5 vendor checks", v2.Count == 0,
            v2.Count == 0 ? "no errors" : $"{v2.Count} error(s): {string.Join(", ", v2.Select(i => i.Rule).Distinct())}"));
        var capture = JsonNode.Parse(File.ReadAllText(CaptureFolder.Find(src, "session.json")))!.AsObject();
        string recordingPath = CaptureFolder.Raw(src, "recording.json");
        var recording = File.Exists(recordingPath) ? JsonNode.Parse(File.ReadAllText(recordingPath))!.AsObject() : null;
        AddRecordingInfo(info, capture, recording, profile);
        if (recording?["audio"]?["status"] is JsonObject audio) AddAudioCheck(checks, Path.Combine(src, "video.mp4"), audio);
        var (_, rows) = Csv.ReadFile(Path.Combine(src, "frames.csv"));
        AddFloorChecks(checks, capture, recording, rows, info, tol, player.StopReason == "permanent_death");

        var frameUtc = PutFramesOnServerTime(round, src, info, checks, tol);
        double? firstCapturedUtc = frameUtc?[0];
        double? cutUtc = new[] { endUtc, GivenUpAt(round, player) }.Min();
        if (frameUtc is not null && !TrimToRound(round, cutUtc, dst, capture, rows, frameUtc, info, checks))
            AddDecodedCheck(checks, Path.Combine(dst, "video.mp4"), rows.Count);
        info["network"] = new JsonObject
        {
            ["ping_ms"] = info["ping_ms"]?.DeepClone(),
            ["jitter_ms"] = info["clock_sync"]?["jitter_ms"]?.DeepClone(),
            ["measured_against"] = "the orchestrator's time service (next to the game server)",
        };
        if (frameUtc is not null && cameras.TryGetValue(player.PlayerId, out var track) &&
            rows.All(r => r[Frames.C2wColumns[0]] == Frames.Null))
            AddServerCamera(rows, frameUtc, track, capture, info, checks, tol, Path.Combine(dst, "frames.csv"));
        AddPackets(round, player, profile, games, src, dst, info, checks);
        var header = new FileHeader { SpecVersion = SpecVersion, SessionId = round.SessionId, PlayerId = player.PlayerId,
            ToolVersion = player.ToolVersion ?? "" };
        FramesBin.Write(Path.Combine(dst, "frames.csv"), frameUtc, header, Path.Combine(dst, FramesBin.FileName));
        if (frameUtc is null) return Finish(dst, info, player.PlayerId, checks);

        info["first_frame_utc"] = UtcText.Iso(frameUtc[0]);
        info["last_frame_utc"] = UtcText.Iso(frameUtc[^1]);
        // The frame on screen when play starts: the one nearest round_start (the same rule as the pre-roll cut).
        double fps = capture["fps"]?.GetValue<double>() ?? 0;
        int roundStartFrame = frameUtc.FindIndex(t => t >= round.StartUtcMs - (fps > 0 ? 500 / fps : 0));
        info["round_start_frame"] = roundStartFrame < 0 ? null : roundStartFrame;
        info["round_start_video_time_ms"] = Math.Round(round.StartUtcMs - frameUtc[0], 1);

        double lateStart = firstCapturedUtc!.Value - round.StartUtcMs;
        checks.Add(new QaCheck("sync.start", $"first frame at most {tol.StartGraceMs} ms after round_start",
            lateStart <= tol.StartGraceMs, $"first frame {lateStart:F0} ms after round_start"));
        if (new[] { player.StopUtcMs ?? round.EndUtcMs, cutUtc }.Min() is double stopAt)
            checks.Add(new QaCheck("sync.end", $"last frame at most {tol.EndGraceMs} ms before the player's stop",
                stopAt - frameUtc[^1] <= tol.EndGraceMs, $"last frame {stopAt - frameUtc[^1]:F0} ms before the stop"));

        (string Key, string File)[] files =
        [
            ("video", "video.mp4"), ("frames_csv", "frames.csv"), ("frames_bin", FramesBin.FileName), ("events", "events.jsonl"),
            ("key_bindings", SessionBuilder.KeyBindingsFile), ("packets_bin", PacketLog.BinFile), ("packets_txt", PacketLog.TextFile),
        ];
        info["files"] = new JsonObject(files.Where(f => f.Key == "events" || File.Exists(Path.Combine(dst, f.File)))
            .Select(f => KeyValuePair.Create(f.Key, (JsonNode?)f.File)));
        PlayerTimeline.WriteEvents(Path.Combine(dst, "events.jsonl"), PlayerTimeline.OnVideo(events, frameUtc));
        return Finish(dst, info, player.PlayerId, checks);
    }

    /// <summary>
    /// The camera from the game server's view of the player when their own capture has none: rewrites frames.csv and checks
    /// the camera floor again on it.
    /// </summary>
    private static void AddServerCamera(List<Dictionary<string, string>> rows, List<double> frameUtc, IServerCameraTrack track,
        JsonObject capture, JsonObject info, List<QaCheck> checks, GateTolerances tol, string framesCsv)
    {
        double leadMs = (info["ping_ms"]?.GetValue<double>() ?? 0) / 2;
        int width = capture["record_width_px"]?.GetValue<int>() ?? 0, height = capture["record_height_px"]?.GetValue<int>() ?? 0;
        var (withCamera, notFirstPerson) = track.Fill(rows, frameUtc, leadMs, width, height);
        WriteFrames(framesCsv, rows);

        info["camera"] = track.Describe(leadMs);
        info["frame_cleanliness"] = new JsonObject
        {
            ["source"] = "game server",
            ["not_first_person_frame_ranges"] = new JsonArray(notFirstPerson.Select(r => (JsonNode)new JsonArray(r.First, r.Last)).ToArray()),
        };
        if (info["frame_stats"] is JsonObject stats) stats["frames_with_camera"] = withCamera;
        double coverage = rows.Count == 0 ? 0 : withCamera / (double)rows.Count;
        checks.RemoveAll(c => c.Id == "floor.camera");
        checks.Add(new QaCheck("floor.camera", $"camera on at least {tol.MinCameraCoverage:P0} of frames when captured",
            coverage >= tol.MinCameraCoverage, $"{coverage:P1} (from the game server; no camera while dead)"));

        if (InputCameraSync(rows) is { } sync)
        {
            info["qa_sync_input_camera"] = sync.Describe();
            checks.Add(new QaCheck("sync.input_camera", "the player's mouse and the server's camera turn together (same frame)",
                sync.Correlation >= 0.8 && sync.BestShift is -1 or 0,
                $"correlation {sync.Correlation:F3} at {sync.BestShift:+0;-0} frame(s)", Required: false));
        }
    }

    /// <summary>
    /// Mouse x against the camera's yaw, frame by frame: a frame's mouse movement (accumulated since the previous frame)
    /// should match the turn into that frame (shift −1). The best shift tells how far the two clocks disagree, in frames.
    /// </summary>
    internal sealed record InputCameraSyncResult(int BestShift, double Correlation, int Frames)
    {
        public JsonObject Describe() => new()
        {
            ["method"] = "correlation of input_mouse_dx with the camera's yaw change (C2W), the camera shifted by -3..+3 frames",
            ["expected_shift_frames"] = -1,
            ["best_shift_frames"] = BestShift,
            ["correlation"] = Math.Round(Correlation, 4),
            ["frames"] = Frames,
        };
    }

    internal static InputCameraSyncResult? InputCameraSync(List<Dictionary<string, string>> rows)
    {
        var yaw = rows.Select(r => r[Frames.C2wColumns[0]] == Frames.Null
            ? (double?)null
            : Math.Atan2(-Frames.ParseDouble(r["C2W_M12"]), -Frames.ParseDouble(r["C2W_M02"])) * 180 / Math.PI).ToList();
        var dx = rows.Select(r => r["input_mouse_dx"] == Frames.Null ? 0 : Frames.ParseDouble(r["input_mouse_dx"])).ToList();
        InputCameraSyncResult? best = null;
        for (int shift = -3; shift <= 3; shift++)
        {
            var pairs = new List<(double Mouse, double Turn)>();
            for (int i = 0; i < rows.Count; i++)
            {
                int j = i + shift;
                if (j < 0 || j + 1 >= rows.Count || yaw[j] is not { } a || yaw[j + 1] is not { } b) continue;
                pairs.Add((dx[i], -(((b - a) % 360 + 540) % 360 - 180)));  // turning right decreases yaw
            }
            if (pairs.Count < 30) continue;
            double r = Correlation(pairs);
            if (best is null || r > best.Correlation) best = new InputCameraSyncResult(shift, r, pairs.Count);
        }
        return best;
    }

    private static double Correlation(List<(double X, double Y)> pairs)
    {
        double mx = pairs.Average(p => p.X), my = pairs.Average(p => p.Y);
        double sxy = pairs.Sum(p => (p.X - mx) * (p.Y - my)), sxx = pairs.Sum(p => (p.X - mx) * (p.X - mx)),
            syy = pairs.Sum(p => (p.Y - my) * (p.Y - my));
        return sxx == 0 || syy == 0 ? 0 : sxy / Math.Sqrt(sxx * syy);
    }

    /// <summary>packets.bin / packets.txt from the player's packets.pcapng (spec v3 §2.3), when the game's profile asks for them.</summary>
    /// <summary>Spec v3 §2.3: a game whose profile has a network section must have its packets, so a missing or empty capture fails the player.</summary>
    private static void AddPackets(RoundRecord round, RoundPlayer player, JsonObject? profile, GameModules games, string src, string dst,
        JsonObject info, List<QaCheck> checks)
    {
        if (profile?["network"] is not JsonObject network) return;
        string pcapng = CaptureFolder.Raw(src, PacketCaptureFiles.Pcapng);
        if (!File.Exists(pcapng))
        {
            checks.Add(new QaCheck("packets.captured", "the game's network traffic was captured (spec v3 §2.3)", false,
                "no packets.pcapng (packet capture declined or failed on this machine)"));
            return;
        }
        string localPath = CaptureFolder.Raw(src, PacketCaptureFiles.LocalAddresses);
        var spec = PacketCaptureSpec.Parse(network, "game profile");
        string protocol = spec.Protocol;
        double offsetMs = info["clock_sync"]?["offset_ms"]?.GetValue<double>() ?? 0;
        try
        {
            int count = PacketLog.Write(pcapng, PacketLog.ReadLocalAddresses(localPath), spec, offsetMs,
                new FileHeader { SpecVersion = SpecVersion, SessionId = round.SessionId, PlayerId = player.PlayerId,
                    ToolVersion = player.ToolVersion ?? "" },
                Path.Combine(dst, PacketLog.BinFile), Path.Combine(dst, PacketLog.TextFile), games.Packets);
            info["packets"] = new JsonObject { ["protocol"] = protocol, ["records"] = count, ["clock_offset_ms"] = offsetMs };
            checks.Add(new QaCheck("packets.captured", "the game's network traffic was captured (spec v3 §2.3)", count > 0,
                count > 0 ? $"{count} records ({protocol})" : $"packets.pcapng holds no {protocol} traffic (wrong adapter or port?)"));
        }
        catch (InvalidDataException e)
        {
            checks.Add(new QaCheck("packets.captured", "the game's network traffic was captured (spec v3 §2.3)", false, e.Message));
        }
    }

    /// <summary>
    /// Where every stream of the round ends when a stop gave up its last frame(s) on some machine: half a frame before the
    /// first stand-in frame of the player with the most (<see cref="GivenUpAt"/>), among the players stopped by the round's
    /// end whose capture did not fail: a failed stream is not delivered as valid and must not shorten the others (a stop
    /// that gave up more than <see cref="CaptureReport.MaxTailSeconds"/> is one). The client allows a delivered round to
    /// end a few seconds before the game's (decided 2026-11-09). Null when every stop got its last frame.
    /// </summary>
    private static double? CommonEnd(RoundRecord round) =>
        round.Players.Where(p => p.StopReason != "permanent_death" && p.Verdict != CaptureReport.Failed &&
                                 p.Report is { } r && r.TailFrames <= CaptureReport.MaxTailSeconds * r.Fps)
            .Select(p => GivenUpAt(round, p)).Min();

    /// <summary>
    /// Half a frame before this player's first stand-in frame (<see cref="CaptureReport.TailFrames"/>: the recorder cut
    /// the video at the round's frames from round_start, the last of which show the frame before); null when the stop
    /// got its last frame.
    /// </summary>
    private static double? GivenUpAt(RoundRecord round, RoundPlayer player)
    {
        if (player.Report is not { TailFrames: > 0, Fps: > 0 } r || (player.StopUtcMs ?? round.EndUtcMs) is not double stop) return null;
        long frames = VideoFinalize.RoundFrames(round.StartUtcMs, stop, r.Fps);
        return round.StartUtcMs + (frames - r.TailFrames - 0.5) * 1000 / r.Fps;
    }

    /// <summary>
    /// Players record from the countdown, a few seconds before play, and spec v3 §1 makes the round the task boundary: the
    /// delivered stream starts at the frame nearest round_start (within half a frame before it, else the first after:
    /// a grid placed on round_start lands ±20 ms around it, and "at or after" would cut frame 0 on half the machines)
    /// and ends before <paramref name="cutUtc"/> when there is one (<see cref="CommonEnd"/>). The video is re-encoded for
    /// a frame-accurate cut (odx record's keyframes are 2–4 s apart), frames.csv is rebased (frame 0, timestamp 0), and
    /// frames.bin and events.jsonl follow from the rebased rows. False when nothing is cut, when the frame indexes cannot
    /// be trusted (video.frame_count already fails) or when the cut failed (video.trimmed fails).
    /// </summary>
    private static bool TrimToRound(RoundRecord round, double? cutUtc, string dst, JsonObject capture, List<Dictionary<string, string>> rows,
        List<double> frameUtc, JsonObject info, List<QaCheck> checks)
    {
        const string check = "the delivered stream starts at round_start and ends where every player captured (pre-roll / end cut)";
        double fps = capture["fps"]?.GetValue<double>() ?? 0;
        double half = fps > 0 ? 500 / fps : 0;
        int n = Math.Max(0, frameUtc.FindIndex(t => t >= round.StartUtcMs - half));
        int last = cutUtc is double cut ? frameUtc.FindIndex(t => t >= cut) : -1;
        if (last < 0) last = rows.Count;
        string video = Path.Combine(dst, "video.mp4");
        if ((n == 0 && last == rows.Count) || fps <= 0 || !File.Exists(video)) return false;
        if (last <= n)
        {
            checks.Add(new QaCheck("video.trimmed", check, false, $"no frame left between round_start and the end cut at {UtcText.Iso(cutUtc!.Value)}"));
            return false;
        }
        Mp4Info mp4;
        try
        {
            mp4 = Mp4Reader.Read(video);
        }
        catch (InvalidDataException)
        {
            return false;
        }
        if (mp4.Frames != rows.Count) return false;
        string ffmpeg = ExternalTools.Ffmpeg(), trimmed = video + ".trim.mp4";
        string? error = null;
        try
        {
            VideoTrim.Trim(ffmpeg, video, trimmed, n, fps, mp4.HasAudio, last < rows.Count ? last : null);
            long decoded = VideoTrim.DecodedFrameCount(ffmpeg, trimmed), samples = Mp4Reader.Read(trimmed).Frames;
            if (decoded != last - n || samples != last - n)
                error = $"the trimmed video has {samples} samples and {decoded} decoded frames, expected {last - n}";
        }
        catch (Exception e) when (e is IOException or InvalidDataException or InvalidOperationException)
        {
            error = e.Message;
        }
        if (error is not null)
        {
            File.Delete(trimmed);
            checks.Add(new QaCheck("video.trimmed", check, false, error));
            return false;
        }
        File.Move(trimmed, video, true);
        int endCut = rows.Count - last;
        rows.RemoveRange(last, endCut);
        frameUtc.RemoveRange(last, endCut);
        long baseMs = long.Parse(rows[n]["timestamp_ms"], CultureInfo.InvariantCulture);
        rows.RemoveRange(0, n);
        frameUtc.RemoveRange(0, n);
        for (int i = 0; i < rows.Count; i++)
        {
            rows[i]["frame_id"] = i.ToString(CultureInfo.InvariantCulture);
            rows[i]["timestamp_ms"] = (long.Parse(rows[i]["timestamp_ms"], CultureInfo.InvariantCulture) - baseMs).ToString(CultureInfo.InvariantCulture);
        }
        WriteFrames(Path.Combine(dst, "frames.csv"), rows);
        info["pre_roll_frames"] = n;
        info["pre_roll_ms"] = baseMs;
        info["end_cut_frames"] = endCut;
        if (info["recording"] is JsonObject recording)
        {
            recording["frame_count"] = rows.Count;
            recording["duration_ms"] = rows.Count * 1000.0 / fps;
        }
        checks.Add(new QaCheck("video.trimmed", check, true,
            $"{n} frames ({baseMs} ms) before round_start cut, {endCut} at the end, {rows.Count} kept; re-encoded, every frame decodes"));
        return true;
    }

    /// <summary>Spec v2 §1.5.6 by decoding, not by the sample table: a recorder's cut-short last sample counts there but not here.</summary>
    private static void AddDecodedCheck(List<QaCheck> checks, string video, int rows)
    {
        if (!File.Exists(video)) return;
        try
        {
            long decoded = VideoTrim.DecodedFrameCount(ExternalTools.Ffmpeg(), video);
            checks.Add(new QaCheck("video.decoded_frame_count", "every frame of video.mp4 decodes, one per frames.csv row",
                decoded == rows, $"{decoded} frames decode, {rows} rows"));
        }
        catch (InvalidOperationException e)
        {
            checks.Add(new QaCheck("video.decoded_frame_count", "every frame of video.mp4 decodes, one per frames.csv row", false, e.Message));
        }
    }

    private static void WriteFrames(string path, List<Dictionary<string, string>> rows)
    {
        using var writer = new StreamWriter(path, false, new System.Text.UTF8Encoding(false)) { NewLine = "\n" };
        writer.Write(Csv.Line(Frames.Columns));
        foreach (var row in rows) writer.Write(Csv.Line(Frames.Columns.Select(c => row[c])));
    }

    /// <summary>The game server's own recording goes next to the delivery, not into it.</summary>
    private static void MoveToWork(string from, string to)
    {
        if (!Directory.Exists(from)) return;
        Directory.CreateDirectory(Path.GetDirectoryName(to)!);
        Directory.Move(from, to);
    }

    private static void AddRecordingInfo(JsonObject info, JsonObject capture, JsonObject? recording, JsonObject? profile)
    {
        info["recording"] = new JsonObject
        {
            ["fps"] = capture["fps"]?.DeepClone(),
            ["frame_count"] = capture["frame_count"]?.DeepClone(),
            ["duration_ms"] = capture["duration_ms"]?.DeepClone(),
            ["record_width_px"] = capture["record_width_px"]?.DeepClone(),
            ["record_height_px"] = capture["record_height_px"]?.DeepClone(),
            ["screen_width_px"] = capture["screen_width_px"]?.DeepClone(),
            ["screen_height_px"] = capture["screen_height_px"]?.DeepClone(),
            ["encoder"] = recording?["encoder"]?.DeepClone(),
            ["capture_source"] = recording?["source"]?.DeepClone(),
            ["duplicate_frames"] = recording?["duplicate_frames"]?.DeepClone(),
            ["dropped_source_frames"] = recording?["dropped_source_frames"]?.DeepClone(),
            ["first_frame_capture_latency_ms"] = recording?["first_frame_capture_latency_ms"]?.DeepClone(),
            ["audio"] = recording?["audio"]?["status"]?.DeepClone(),
        };
        info["input_mouse_convention"] = capture["input_mouse_convention"]?.DeepClone();
        info["key_bindings"] = profile?["bindings"]?.DeepClone();
    }

    /// <summary>Advisory: audio was asked for, so the video should carry the game's sound, not silence or nothing.</summary>
    private static void AddAudioCheck(List<QaCheck> checks, string videoPath, JsonObject audio)
    {
        string source = audio["source"]?.GetValue<string>() ?? AudioStatus.NotRecorded;
        double? silent = audio["silent_ratio"]?.GetValue<double>();
        bool track = false;
        try
        {
            track = File.Exists(videoPath) && Mp4Reader.Read(videoPath).HasAudio;
        }
        catch (InvalidDataException)
        {
        }
        checks.Add(new QaCheck("audio.track", "the video carries the game's sound (odx record --audio)",
            source != AudioStatus.NotRecorded && track && silent is not > 0.99,
            $"source {source}{(audio["fallback_reason"] is { } f ? $" (fallback: {f})" : "")}, track {(track ? "present" : "missing")}" +
            $"{(silent is double s ? $", {s:P0} silent" : "")}{(audio["error"] is { } e ? $", error: {e}" : "")}", Required: false));
    }

    /// <summary>Spec v3 §2 floors (720p/30) and the sample delivery's 10 s / camera-coverage checks; window-only capture is advisory.</summary>
    /// <param name="diedForGood">Stopped by a permanent death: spec v3 §1 wants the recording cut there, so a short one is
    /// not a fault and the duration floor does not apply.</param>
    private static void AddFloorChecks(List<QaCheck> checks, JsonObject capture, JsonObject? recording,
        List<Dictionary<string, string>> rows, JsonObject info, GateTolerances tol, bool diedForGood)
    {
        double fps = capture["fps"]?.GetValue<double>() ?? 0;
        int width = capture["record_width_px"]?.GetValue<int>() ?? 0, height = capture["record_height_px"]?.GetValue<int>() ?? 0;
        double durationMs = capture["duration_ms"]?.GetValue<double>() ?? 0;
        checks.Add(new QaCheck("floor.fps", $"at least {tol.MinFps} fps", fps >= tol.MinFps, $"{fps} fps"));
        checks.Add(new QaCheck("floor.resolution", $"at least {tol.MinWidth}x{tol.MinHeight}",
            width >= tol.MinWidth && height >= tol.MinHeight, $"{width}x{height}"));
        checks.Add(new QaCheck("floor.duration", $"at least {tol.MinDurationMs / 1000} s (unless stopped by a permanent death)",
            durationMs >= tol.MinDurationMs || diedForGood, $"{durationMs / 1000:F1} s{(diedForGood ? ", permanent death" : "")}"));

        int withCamera = rows.Count(r => r[Frames.C2wColumns[0]] != Frames.Null);
        int withKeys = rows.Count(r => r["input_keys"] != Frames.Null);
        int withMouse = rows.Count(r => r["input_mouse_dx"] is not ("0.0" or "null") || r["input_mouse_dy"] is not ("0.0" or "null"));
        info["frame_stats"] = new JsonObject
        {
            ["frames"] = rows.Count,
            ["frames_with_camera"] = withCamera,
            ["frames_with_keys"] = withKeys,
            ["frames_with_mouse"] = withMouse,
        };
        double coverage = rows.Count == 0 ? 0 : withCamera / (double)rows.Count;
        checks.Add(new QaCheck("floor.camera", $"camera on at least {tol.MinCameraCoverage:P0} of frames when captured",
            withCamera == 0 || coverage >= tol.MinCameraCoverage,
            withCamera == 0 ? "no camera data (camera columns are optional)" : $"{coverage:P1}"));

        string source = recording?["source"]?.GetValue<string>() ?? "unknown";
        checks.Add(new QaCheck("capture.window", "only the game window is captured (no other windows or notifications)",
            source.StartsWith("window:", StringComparison.Ordinal), source, Required: false));
    }

    /// <summary>Server UTC of each video frame, from the samples against the orchestrator's time service; null without them.</summary>
    private static List<double>? PutFramesOnServerTime(RoundRecord round, string src, JsonObject info, List<QaCheck> checks,
        GateTolerances tol)
    {
        if (PlayerTimeline.ServerClock(src, round.TimePort) is not var (samples, best, map))
        {
            checks.Add(new QaCheck("sync.clock", "clock measured against the orchestrator's time service", false,
                $"no clock samples against the time service (port {round.TimePort})"));
            return null;
        }
        double rtt = Median(samples.Select(s => s.RttMs));
        info["ping_ms"] = Math.Round(rtt, 1);
        info["clock_sync"] = ClockSync(samples, best, map);
        checks.Add(new QaCheck("sync.clock", $"median RTT to the time service at most {tol.MaxRttMs} ms", rtt <= tol.MaxRttMs,
            $"median RTT {rtt:F1} ms over {samples.Count} samples"));
        AddSystemClock(round, src, (JsonObject)info["clock_sync"]!, Median(samples.Select(s => s.SystemOffsetMs)), checks, tol);

        var frameUtc = PlayerTimeline.FrameUtc(src, map);
        if (frameUtc.Count > 0) return frameUtc;
        checks.Add(new QaCheck("player.frames", "the video has frames", false, "no video frames"));
        return null;
    }

    /// <summary>
    /// Spec v3 §4 layer 1 for this player: the Windows clock against NTP during the round = (orchestrator − player, the
    /// round's samples) + (NTP − orchestrator, the orchestrator's own measurement), plus the agent's pre-round check when
    /// its clock_check.json is there.
    /// </summary>
    private static void AddSystemClock(RoundRecord round, string src, JsonObject clockSync, double offsetToOrchestratorMs,
        List<QaCheck> checks, GateTolerances tol)
    {
        string checkPath = CaptureFolder.Raw(src, ClockCheck.FileName);
        var system = File.Exists(checkPath) ? PlayerFile.SystemClock(ClockCheck.Load(checkPath)) : new JsonObject();
        system["tolerance_ms"] = tol.MaxSystemClockOffsetMs;
        if (round.ServerClock is not { } sc)
        {
            system["round_offset_ms"] = null;
            clockSync["system_clock"] = system;
            checks.Add(new QaCheck("sync.system_clock", $"Windows clock within {tol.MaxSystemClockOffsetMs} ms of NTP during the round",
                false, "the orchestrator did not measure its clock against NTP, so the player's cannot be known"));
            return;
        }
        double offset = offsetToOrchestratorMs + sc.OffsetMs;
        system["round_offset_ms"] = Math.Round(offset, 3);
        system["round_offset_definition"] = "ntp_clock - player_clock, via the orchestrator";
        clockSync["system_clock"] = system;
        checks.Add(new QaCheck("sync.system_clock", $"Windows clock within {tol.MaxSystemClockOffsetMs} ms of NTP during the round",
            Math.Abs(offset) <= tol.MaxSystemClockOffsetMs,
            $"NTP - player clock {offset:+0.0;-0.0} ms (orchestrator - player {offsetToOrchestratorMs:+0.0;-0.0} ms, NTP - orchestrator {sc.OffsetMs:+0.0;-0.0} ms)"));
    }

    /// <summary>
    /// How this player's frames were put on server time: the SNTP samples against the orchestrator's time service (offset =
    /// server clock − player's Windows clock), the lower-RTT half fitted to a QPC → server-UTC line (drift = its QPC rate
    /// error, only over ≥ 60 s), and jitter as the mean change of RTT between consecutive samples.
    /// </summary>
    private static JsonObject ClockSync(List<NtpSample> samples, List<NtpSample> best, ClockMap map) => new()
    {
        ["method"] = "SNTP against the orchestrator's time service; least-squares QPC -> server UTC over the lower-RTT half",
        ["offset_definition"] = "server_clock - player_clock",
        ["offset_ms"] = Math.Round(Median(best.Select(s => s.SystemOffsetMs)), 3),
        ["rtt_ms"] = Math.Round(Median(samples.Select(s => s.RttMs)), 3),
        ["jitter_ms"] = samples.Count > 1
            ? Math.Round(samples.Zip(samples.Skip(1), (a, b) => Math.Abs(b.RttMs - a.RttMs)).Average(), 3)
            : null,
        ["drift_us_per_s"] = map.Slope != 1 ? Math.Round((map.Slope - 1) * 1e6, 3) : null,
        ["sample_count"] = samples.Count,
        ["samples"] = new JsonArray(samples.Select(s => (JsonNode)new JsonObject
        {
            ["utc"] = UtcText.Iso(s.UtcUnixMs),
            ["offset_ms"] = Math.Round(s.SystemOffsetMs, 3),
            ["rtt_ms"] = Math.Round(s.RttMs, 3),
        }).ToArray()),
    };

    private static PlayerReport Finish(string dst, JsonObject info, string playerId, List<QaCheck> checks)
    {
        bool pass = checks.All(c => c.Pass || !c.Required);
        info["valid"] = pass;
        info["qa"] = new JsonObject
        {
            ["stream_valid"] = pass,
            ["checks"] = JsonSerializer.SerializeToNode(checks, AgentMessage.Json),
        };
        File.WriteAllText(Path.Combine(dst, "player.json"), info.ToJsonString(RoundRecord.PrettyJson));
        var issues = checks.Where(c => !c.Pass)
            .Select(c => c.Required ? Issue.Error(c.Id, c.Detail) : Issue.Warning(c.Id, c.Detail)).ToList();
        return new PlayerReport(playerId, pass, issues);
    }

    public static List<JsonObject> ReadEvents(string path) =>
        File.Exists(path)
            ? File.ReadLines(path).Where(l => l.Trim().Length > 0).Select(l => JsonNode.Parse(l)!.AsObject()).ToList()
            : [];

    public static string Slug(string text) =>
        string.Concat(text.ToLowerInvariant().Select(c => char.IsAsciiLetterOrDigit(c) ? c : '-')).Trim('-');

    private static double Median(IEnumerable<double> values)
    {
        var sorted = values.Order().ToList();
        return sorted[sorted.Count / 2];
    }
}
