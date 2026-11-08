using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using Odx.Protocol;

namespace Odx.Orchestrator;

/// <summary>A room as stored: what an orchestrator started again needs to take it back.</summary>
public sealed record StoredRoom(string Code, string? Host, string? Server, string? LanNetworkId, string? MatchId, string? Map,
    string Game, int RoundNumber, List<JsonObject> BetweenRounds);

/// <param name="State">open (recording), ended (waiting for its participants), finished (files sent) or cancelled.</param>
public sealed record StoredRound(string SessionId, string Room, string State, bool Counting, string? Server, bool ServerDone,
    RoundRecord Record, List<JsonObject> Events);

/// <summary>
/// The orchestrator's state in SQLite, written as it changes: rooms, rounds and their players. After a restart the open
/// rooms and unfinished rounds come back, and the rounds stay as a log (how many hours, by whom, kept or not).
/// One connection, used under the coordinator's lock.
/// </summary>
public sealed class OrchestratorStore : IDisposable
{
    private readonly SqliteConnection _db;
    private SqliteTransaction? _tx;

    public OrchestratorStore(string path)
    {
        if (Path.GetDirectoryName(Path.GetFullPath(path)) is { } dir) Directory.CreateDirectory(dir);
        _db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path }.ToString());
        _db.Open();
        Execute("""
            PRAGMA journal_mode = WAL;
            PRAGMA synchronous = NORMAL;
            CREATE TABLE IF NOT EXISTS meta (key TEXT PRIMARY KEY, value TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS rooms (
                code TEXT PRIMARY KEY, host TEXT, server TEXT, lan_network TEXT, match_id TEXT, map TEXT, game TEXT NOT NULL,
                round_number INTEGER NOT NULL, between_rounds TEXT NOT NULL, opened_utc TEXT NOT NULL, closed_utc TEXT);
            CREATE TABLE IF NOT EXISTS rounds (
                session_id TEXT PRIMARY KEY, room TEXT NOT NULL, state TEXT NOT NULL, counting INTEGER NOT NULL, server TEXT,
                server_done INTEGER NOT NULL, start_utc_ms REAL NOT NULL, end_utc_ms REAL, record TEXT NOT NULL,
                events TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS round_players (
                session_id TEXT NOT NULL, player_id TEXT NOT NULL, name TEXT, stop_reason TEXT, stop_utc_ms REAL,
                done INTEGER NOT NULL, kept INTEGER NOT NULL, error TEXT, PRIMARY KEY (session_id, player_id));
            """);
    }

    public long NextEventId
    {
        get => Scalar("SELECT value FROM meta WHERE key = 'event_id'") is string v ? long.Parse(v) : 0;
        set => Execute("INSERT INTO meta VALUES ('event_id', $v) ON CONFLICT(key) DO UPDATE SET value = $v", ("$v", value.ToString()));
    }

    /// <summary>A new room drawing the code of a closed one (codes are random, only open rooms are checked) reopens its row.</summary>
    public void SaveRoom(StoredRoom room) =>
        Execute("""
            INSERT INTO rooms VALUES ($code, $host, $server, $lan, $match, $map, $game, $round, $between, $now, NULL)
            ON CONFLICT(code) DO UPDATE SET host = $host, server = $server, lan_network = $lan, match_id = $match, map = $map,
                game = $game, round_number = $round, between_rounds = $between,
                opened_utc = CASE WHEN closed_utc IS NULL THEN opened_utc ELSE $now END, closed_utc = NULL
            """,
            ("$code", room.Code), ("$host", room.Host), ("$server", room.Server), ("$lan", room.LanNetworkId),
            ("$match", room.MatchId), ("$map", room.Map), ("$game", room.Game), ("$round", room.RoundNumber),
            ("$between", Jsonl(room.BetweenRounds)), ("$now", DateTime.UtcNow.ToString("o")));

    public void CloseRoom(string code) =>
        Execute("UPDATE rooms SET closed_utc = $now WHERE code = $code AND closed_utc IS NULL",
            ("$code", code), ("$now", DateTime.UtcNow.ToString("o")));

    public void SaveRound(StoredRound round)
    {
        using var tx = _tx = _db.BeginTransaction();
        try
        {
            WriteRound(round);
            tx.Commit();
        }
        finally
        {
            _tx = null;
        }
    }

    private void WriteRound(StoredRound round)
    {
        Execute("""
            INSERT INTO rounds VALUES ($sid, $room, $state, $counting, $server, $serverDone, $start, $end, $record, $events)
            ON CONFLICT(session_id) DO UPDATE SET room = $room, state = $state, counting = $counting, server = $server,
                server_done = $serverDone, start_utc_ms = $start, end_utc_ms = $end, record = $record, events = $events
            """,
            ("$sid", round.SessionId), ("$room", round.Room), ("$state", round.State), ("$counting", round.Counting),
            ("$server", round.Server), ("$serverDone", round.ServerDone), ("$start", round.Record.StartUtcMs),
            ("$end", round.Record.EndUtcMs), ("$record", JsonSerializer.Serialize(round.Record, AgentMessage.Json)),
            ("$events", Jsonl(round.Events)));
        // A cancelled round's session id is used again: the round that reuses it starts with its own players.
        Execute("DELETE FROM round_players WHERE session_id = $sid", ("$sid", round.SessionId));
        foreach (var p in round.Record.Players)
            Execute("INSERT INTO round_players VALUES ($sid, $player, $name, $reason, $stop, $done, $kept, $error)",
                ("$sid", round.SessionId), ("$player", p.PlayerId), ("$name", p.Name), ("$reason", p.StopReason),
                ("$stop", p.StopUtcMs), ("$done", p.Done), ("$kept", p.Kept), ("$error", p.Error));
    }

    public List<StoredRoom> OpenRooms() =>
        Query("SELECT code, host, server, lan_network, match_id, map, game, round_number, between_rounds FROM rooms WHERE closed_utc IS NULL",
            r => new StoredRoom(r.GetString(0), Text(r, 1), Text(r, 2), Text(r, 3), Text(r, 4), Text(r, 5), r.GetString(6), r.GetInt32(7),
                Events(r.GetString(8))));

    public List<StoredRound> UnfinishedRounds() => Rounds("WHERE state IN ('open', 'ended')");

    public StoredRound? Round(string sessionId) => Rounds("WHERE session_id = $sid", ("$sid", sessionId)).SingleOrDefault();

    private List<StoredRound> Rounds(string where, params (string Name, object? Value)[] parameters) =>
        Query($"SELECT session_id, room, state, counting, server, server_done, record, events FROM rounds {where}",
            r => new StoredRound(r.GetString(0), r.GetString(1), r.GetString(2), r.GetBoolean(3), Text(r, 4), r.GetBoolean(5),
                JsonSerializer.Deserialize<RoundRecord>(r.GetString(6), AgentMessage.Json)!, Events(r.GetString(7))), parameters);

    public void Dispose() => _db.Dispose();

    private static string Jsonl(IEnumerable<JsonObject> events) => RoundRecord.EventsJsonl(events);

    private static List<JsonObject> Events(string jsonl) =>
        jsonl.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => JsonNode.Parse(l)!.AsObject()).ToList();

    private static string? Text(SqliteDataReader r, int i) => r.IsDBNull(i) ? null : r.GetString(i);

    private void Execute(string sql, params (string Name, object? Value)[] parameters)
    {
        using var command = Command(sql, parameters);
        command.ExecuteNonQuery();
    }

    private object? Scalar(string sql)
    {
        using var command = Command(sql, []);
        return command.ExecuteScalar();
    }

    private List<T> Query<T>(string sql, Func<SqliteDataReader, T> read, params (string Name, object? Value)[] parameters)
    {
        using var command = Command(sql, parameters);
        using var reader = command.ExecuteReader();
        var rows = new List<T>();
        while (reader.Read()) rows.Add(read(reader));
        return rows;
    }

    private SqliteCommand Command(string sql, (string Name, object? Value)[] parameters)
    {
        var command = _db.CreateCommand();
        command.CommandText = sql;
        command.Transaction = _tx;
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return command;
    }
}
