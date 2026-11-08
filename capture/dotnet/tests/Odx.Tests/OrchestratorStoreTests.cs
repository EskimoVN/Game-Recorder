using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using Odx.Orchestrator;
using Odx.Protocol;
using Xunit;

namespace Odx.Tests;

public sealed class OrchestratorStoreTests
{
    private const double Day = 1_791_331_200_000;  // 2026-10-07 00:00 UTC

    [Fact]
    public void Rooms_and_unfinished_rounds_come_back_and_finished_ones_count_as_hours()
    {
        string path = Path.Combine(Cs2Fixtures.TempDir(), "orchestrator.db");
        using (var store = new OrchestratorStore(path))
        {
            store.NextEventId = 41;
            store.SaveRoom(new StoredRoom("ABC234", "p1", "s1", null, "m1", "de_dust2", "Counter-Strike 2", 2,
                [new JsonObject { ["type"] = "match_start" }]));
            store.SaveRoom(new StoredRoom("GONE22", null, null, null, null, null, "Synthetic", 0, []));
            store.CloseRoom("GONE22");
            store.SaveRound(Round("m1-r01", "finished", Day, Day + 3_600_000,
                new RoundPlayer("p1", "P1", "round_end", null, true, true), new RoundPlayer("p2", "P2", "permanent_death", Day + 1_800_000, true, true),
                new RoundPlayer("p3", "P3", "round_end", null, true, false, "capture failed")));
            store.SaveRound(Round("m1-r02", "open", Day + 4_000_000, null, new RoundPlayer("p1", "P1")));
        }

        using (var store = new OrchestratorStore(path))
        {
            Assert.Equal(41, store.NextEventId);
            var room = Assert.Single(store.OpenRooms());
            Assert.Equal(("ABC234", "p1", "s1", 2), (room.Code, room.Host, room.Server, room.RoundNumber));
            Assert.Equal("match_start", (string?)room.BetweenRounds.Single()["type"]);
            var round = Assert.Single(store.UnfinishedRounds());
            Assert.Equal("m1-r02", round.SessionId);
            Assert.Equal("p1", round.Record.Players.Single().PlayerId);
            Assert.Equal("kill", (string?)round.Events.Single()["type"]);
        }

        string sql = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "..", "guides", "orchestrator.md"));
        sql = sql[(sql.IndexOf("```sql", StringComparison.Ordinal) + 6)..];
        sql = sql[..sql.IndexOf("```", StringComparison.Ordinal)];
        using var db = new SqliteConnection($"Data Source={path}");
        db.Open();
        using var query = db.CreateCommand();
        query.CommandText = sql;
        using var rows = query.ExecuteReader();
        var hours = new Dictionary<string, double>();
        while (rows.Read())
        {
            Assert.Equal("2026-10-07", rows.GetString(0));
            hours[rows.GetString(1)] = rows.GetDouble(2);
        }
        Assert.Equal(new Dictionary<string, double> { ["p1"] = 1.0, ["p2"] = 0.5 }, hours);
    }

    [Fact]
    public void A_closed_rooms_code_drawn_again_is_an_open_room_with_the_new_host()
    {
        string path = Path.Combine(Cs2Fixtures.TempDir(), "orchestrator.db");
        using var store = new OrchestratorStore(path);
        store.SaveRoom(new StoredRoom("ABC234", "p1", null, "lan1", "m1", null, "Synthetic", 3, []));
        store.CloseRoom("ABC234");
        Assert.Empty(store.OpenRooms());

        store.SaveRoom(new StoredRoom("ABC234", "p2", null, null, null, null, "Synthetic", 0, []));

        var room = Assert.Single(store.OpenRooms());
        Assert.Equal(("ABC234", "p2", null, null, 0), (room.Code, room.Host, room.LanNetworkId, room.MatchId, room.RoundNumber));
    }

    private static StoredRound Round(string sid, string state, double start, double? end, params RoundPlayer[] players) =>
        new(sid, "ABC234", state, false, null, false, new RoundRecord(sid, "m1", "Counter-Strike 2", "de_dust2", 1, start, end, 12300,
            [.. players]), [new JsonObject { ["type"] = "kill" }]);
}
