# Orchestrator and odx merge

`odx-orchestrator` runs the rooms and rounds: it tells every player's capture and the game server agent when to start and
stop, runs the time service everybody measures their clock against, and gives rooms a LAN when asked. It stores no files:
when a round is over it sends each participant the round's files (`round.json`, `server_events.jsonl`,
`game_profile.json`), which they keep next to their recording.

It does not need to be next to the CS2 server (its agent, `odx game-server`, only connects to it:
[cs2-server.md](cs2-server.md#6-the-game-server-agent)). On a Linux server, `deploy/` runs it behind HTTPS (`README.md`,
"Deploy the orchestrator"). On Windows, it is in a package built with `-WithOrchestrator` (`bin\`).

The sessions to hand over are built afterwards by `odx merge`, from the rounds copied together by hand
([below](#build-the-sessions)).

## Get it

`odx-orchestrator.exe` is in the package (`bin\`), next to `odx.exe`, `games\cs2.json` and `odx-capture.json`, when
the package is built with `dotnet/publish.ps1 -WithOrchestrator` (see [player-setup.md](player-setup.md#1-build-the-package));
the players' package leaves it out.

## Run

```powershell
$bin = 'D:\odx\odx-capture-0.1.0-win-x64\bin'
& "$bin\odx-orchestrator.exe" --game-profile "$bin\games\cs2.json" --access-key <access key>
```

| Option | |
|---|---|
| `--game-profile` | The default game, e.g. `games\cs2.json`. Every profile in that folder is available: a room plays the game its host had selected in odx-app when opening it, the default when the host had none or a game server plugin opened the room. |
| `--access-key` | Or `ODX_ACCESS_KEY`. Every request must carry it; put the same key in the players' package and give it to the game server agent. Use one whenever the orchestrator is reachable from the internet. |
| `--http-port`, `--time-port` | Default 5180 (TCP) and 12300 (UDP). |
| `--fps` | The players' recording rate; default 30. |
| `--reconnect-grace-s` | How long a player or game server that lost its connection keeps its place in a running round, recording on; default 30. Back within it, the round goes on; still gone after it, that player is stopped there (and stops by itself). |
| `--state` | The SQLite file with the rooms, the rounds and their players; default `data\orchestrator.db` of the package (`deploy/`: `deploy/state/orchestrator.db`). Restarted, the orchestrator takes back its open rooms and unfinished rounds, and the players reconnecting within the grace record on; a round that was counting down is cancelled. |

The orchestrator also measures its own clock against `time_server` of the `odx-capture.json` next to it
(`time.windows.com` by default) and writes the result into each round's record.

## Check that it runs

```powershell
curl.exe http://127.0.0.1:5180/status -H "Authorization: Bearer <access key>"
# {"rooms":[],"agents_outside_rooms":[],"game_servers":[{"id":"game-server","name":"CS2 server"}]}
```

`game_servers` lists the connected game server agents (empty: rounds have no server side). A room records with the
one it was opened for (`POST /rooms?server=<id>`, shown as the room's `server`), else with the only one connected; with
several connected, a room opened without one gets none. When players are
connected, `/status` lists the rooms, their members and states (`idle`, `armed`, `starting`, `recording`), the rounds
still waiting for their participants (`pending_sessions`) and the finished ones (`sessions`: `session_id`, `players`,
`kept`).

## Log lines to know

| Line | Meaning |
|---|---|
| `game server agent CS2 server (0.1.0) connected` | The CS2 server's side will be recorded from the next round on. |
| `room XXXXXX opened by <player>` | A host created a room; its invite code is XXXXXX. |
| `round <session>: N player(s), play starts in 3 s` | The host started a round; the game server agent starts SourceTV now. |
| `<player> is <state>, left out of <session>` | **Warning**: that player was still busy with the previous round. |
| `game server agent, <session>: …` | **Warning**: the server's side of that round was not kept (the reason follows). |
| `round <session>: 2/2 players kept their recording` | Everyone is done; the round's files went to every participant. |

## Load

One orchestrator is far from its limit at the SOW's pace (about 13–26 rooms recording at once). Measured 2026-10-08 on a
6-core i7-8750H laptop, with the 200 fake players on the same machine: 100 rooms of 2 players, 3 rounds of 20 s, one
event a second per room, every player's clock sampled every second, state in SQLite. All 300 rounds were kept by both
players; the orchestrator used 13% of one core on average (bursts to 94% when all 100 rooms start or end at once) and
168 MB at most; round_start reached the players' captures in 266 ms (p50; p95 565 ms), round_end in 115 ms (p95 310 ms);
event posts took 58 ms (p50; p95 183 ms) with 100 arriving together. Run it again (published orchestrator):

```
ODX_LOAD=<odx-orchestrator.exe> ODX_LOAD_ROOMS=100 ODX_LOAD_ROUNDS=3 ODX_LOAD_ROUND_S=20 dotnet test --filter LoadTests --logger "console;verbosity=detailed"
```

## Hours recorded

Every round stays in the state file: `rounds` (session, room, state `open` / `ended` / `finished` / `cancelled`, start
and end, the round record and events) and `round_players` (stop reason and time, done, kept). Hours each player kept,
per day (UTC), with the `sqlite3` tool:

```sql
SELECT date(r.start_utc_ms / 1000, 'unixepoch') AS day, p.player_id,
       round(sum((coalesce(p.stop_utc_ms, r.end_utc_ms) - r.start_utc_ms) / 3600000.0), 2) AS hours
FROM rounds r JOIN round_players p USING (session_id)
WHERE r.state = 'finished' AND p.kept
GROUP BY day, p.player_id ORDER BY day;
```

These are the players' own recordings; the gate (`odx merge`) decides which of them make a delivered session.

## Build the sessions

Each player keeps a round in the package's `data\captures\<session>\` (`video.mp4`, `session.json`, `frames.csv`,
`events.jsonl` on top; every raw log, the round's files and `manifest.json` in `raw\`), the game server agent in
`<work>\<session>\`.
For now they are copied by hand (upload to a server is a later step) into one folder per round, on any machine with the
package (or the `net10.0` build of `odx`):

```
<test>\<session>\
  round.json  server_events.jsonl  game_profile.json     (from any participant: all got the same; a player's are in raw\)
  players\<pid>\                                          (each player's data\captures\<session>\)
  server\                                                 (the game server's <work>\<session>\server\)
```

```powershell
& "$bin\odx.exe" merge D:\odx\test [--out DIR]
# PASS  <session>  2/2 players valid, 2 required -> ...\data\delivery\odx\<mm-dd-yyyy>\counter-strike-2\<session>
```

It builds every session folder in `<test>` (a folder with `round.json`; `<test>` itself may be one) into
`<out>\odx\<mm-dd-yyyy>\<game>\<session>\`, by default under the package's `data\delivery\` (what to hand over), and
prints PASS/FAIL per session with its issues (e.g. `[warning] server.capture: SourceTV is not recording …`, see
[troubleshooting.md](troubleshooting.md#sourcetv-is-not-recording)). Exit code 1 if any session fails, 2 on bad usage or
when there is no session. Plan disk space: about 2 MB per second of video per player.

Keep the copied rounds: a newer version can build the sessions again from them with the same command.
