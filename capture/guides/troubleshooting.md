# Troubleshooting

## SourceTV is not recording

Game server agent log: `CS2 server: <session>: SourceTV is not recording …`; the session gets a `server.capture` warning
(`odx merge` prints it) and has no `server_state.bin`.

CS2 counts SourceTV as a bot: a match restart (including `changelevel` to the map already running, which only restarts
the match) or a bot quota change can kick it, and `tv_record` then records nothing without saying so. SourceTV comes back
only with a map load:

```
map de_dust2
```

Players are disconnected by it; they connect again. Then check with `tv_record replays/check` (a `check.dem` appears),
`tv_stoprecord`. Add bots with `bot_add_t` / `bot_add_ct` rather than changing the quota mode.

If the log also has `CS2 server: tv_record …: No connection could be made`, the game server agent never reached RCON: CS2
binds RCON to the machine's network address, not to 127.0.0.1. Check with `netstat -ano | findstr :27015` (the TCP line)
and pass that address to `odx game-server --cs2-rcon`.

If the orchestrator's `/status` has an empty `"game_servers"`, no agent is connected and rounds have no server side at all:
start `odx game-server` ([cs2-server.md](cs2-server.md#6-the-game-server-agent)). With several connected, a room opened
without `?server=<id>` gets none either (the orchestrator logs `no game server records it`).

## CS2 server exits while loading the map

A `cs2_*.mdmp` next to `cs2.exe` reads `Failed to initialize Steamworks SDK for gameserver. Could not determine Steam
client install directory`: the machine has no Steam client. Copy `steamclient64.dll`, `tier0_s64.dll` and
`vstdlib_s64.dll` from the SteamCMD folder to `game\bin\win64\`.

## Demos or console.log land in addons\metamod

The `Game csgo/addons/metamod` line of `gameinfo.gi` makes that folder the engine's write path, so `tv_record
replays/…` writes to `addons\metamod\replays\`. Join it to `csgo\replays` (as administrator):

```powershell
New-Item -ItemType Junction -Path D:\cs2-server\game\csgo\addons\metamod\replays -Target D:\cs2-server\game\csgo\replays
```

## The demo ends early or misses the round's end

`tv_delay` is not 0: the game mode's config sets it (30–105 s). Check `tv_delay` in the console; create the
`gamemode_<mode>_server.cfg` of [cs2-server.md](cs2-server.md#4-sourcetv-configs) for the mode you play.

## A player is not linked to the server's data

`session.json` → `players[].steam_id` is null or the same for both players, and their frames have no camera:

- Steam was not running (or not signed in) on that PC when the app connected to the orchestrator: the app reads the
  signed-in account from Steam once, when it connects. Start Steam, then restart the app and join the room again.
- Both players use the same Steam account: one account cannot be told apart; use two.

## The video shows the server's console window

The dedicated server runs on that player's PC and its console window (also `cs2.exe`) was recorded: hide it
([cs2-server.md](cs2-server.md#a-player-hosting-the-server)).

## Session FAIL: floor.duration

The recording is shorter than 10 s; play longer before ending the round.

## Session FAIL: sync.clock

Median RTT to the time service above 100 ms: a slow or distant link to the server (see [network.md](network.md)), or UDP
12300 is blocked (then there are no samples at all).

## A round's folder has video_raw.mp4 and audio.wav but no video.mp4

The recorder was killed (odx-app closed, PC off) before it could finalize the video, or the finalize failed
(`data\logs\odx-app-<date>.log`: `video not finalized: …`, or `recording.json` → `finalize.error`). The parts are the
whole round: `video_raw.mp4` plays as it is (fragmented MP4, video only) and `audio.wav` is the sound from video frame 0.
Make the delivered video from them, then build the session:

```
odx build <round folder> --profile games\<game>.json
```

`odx build` finalizes first (`finalized video.mp4: N frames with sound`), cut to the round's frames when `recording.json`
carries the round's times, and deletes the parts once `video.mp4` stands. A `video.mp4 has N frames, expected M` message
means ffmpeg was killed while recording: the video is playable but short of the last fragment (≤ 4 s), and the round
fails validation.

## The video is frozen or jumps: the encoder cannot keep up

The `stopped` line in `data\logs\odx-app-<date>.log` (or `recording.json`) has `starved` in the hundreds (captured frames
dropped because the encoder had not returned a buffer), `encode_write_ms` near the recording's length, `duplicates`
close to `frames`, and odx-app showed `⚠ encoder behind` on that player during the round (the round notice says
`degraded`). `duplicates` alone proves nothing: a player standing still, a loading screen or a game rendering under 30 fps
duplicate frames with a healthy encoder; `starved` is the sign. The game renders fine; the video encoder (NVENC through
ffmpeg) on that PC takes far longer than a frame per frame, so the recorder keeps showing the last frame it got through.
Seen on 2026-11-07 on a PC recording Arma Reforger at 1768x992: 435 starved, 1430 of 1475 frames duplicated, the picture
changing once a second.

Check it on that PC, while a test round records: Task Manager → Performance → GPU, set one graph to *Video Encode* and
look at *3D*. *3D* pinned at 100 % with *Video Encode* low and ragged means the game takes the whole GPU and the encoder
gets its leftovers. Fixes, in order:

1. Run the game in a smaller window: 1280x720 is 2.6× fewer pixels than 1768x992 and 2.25× fewer than 1080p.
   (until `starved` in the next round's `stopped` line is a small share of `frames`)
2. Cap the game's frame rate (60 fps in its video settings) so the GPU has slack between frames.
3. A laptop: on mains, and in the NVIDIA Control Panel set both the game and `odx.exe` to the high-performance GPU.
4. Update the NVIDIA driver; close other software that uses NVENC (OBS, Discord streaming, GeForce Experience instant
   replay / ShadowPlay): each takes an encoder session.
5. Last resort, when the CPU has cores to spare: `odx record --encoder x264` records on the CPU (odx-app does not expose
   this; run a solo test with `odx record` to see whether it keeps up).

The pre-round check that catches this before a real round: the host runs a 1-minute test round and reads the notice;
every player with `degraded` or `failed` is named on every machine (`all OK` otherwise).

## Reporting a failed or degraded round

The round notice (top right, and the status line) names every player to record again (`failed`) or to check
(`degraded`), with the problem behind it; the same line is in every machine's daily log (`round <session>: …`). What
to send with a report, from the named player's PC:

- `data\logs\odx-app-<date>.log` (the whole day).
- The round folder's `raw\recording.json` and `raw\round.json`, and `player.json`.
- For a frozen video: the Task Manager observation above and the PC's GPU and CPU (dxdiag), desktop or laptop.
- For a stop that stuck (`stop stuck in '<step>'` or `stop: the capture callback did not let go` in the log): Windows
  build (`winver`), GPU driver version, audio device.

A quick self-check before a session, on any PC: open the game, join a room, go Ready and let the host run a 1-minute
test round. The notice says `every player kept it` or names the problem.

## "player id 'X' is in use on another machine"

Two machines signed in with the same Player ID: the orchestrator keeps one connection per id, so the Start and Stop of a
round reach whichever machine connected last and the other one records on without ever reporting (2026-11-08: seven
rounds of "alan" lost this way). The second machine is now turned away with this message in its status line; set a Player
ID of its own in its settings. A machine replacing one that is gone (closed, or its network lost for 15 s) gets in.

## "stop: the capture callback did not let go" / "the GPU did not hand over the last frame"

At the stop the recorder reads the last captured frame back from the GPU. On 2026-11-08/09 that step hung for good on 1 %
of the stops (54 rounds, 21 machines, no pattern by PC, host or round length): the watchdog ended the recorder after 40 s
with the last frames missing (`739/743 frames`) and the room waited that long for the verdict. From 0.3.0 the stop waits
2 s for the capture callback and for the GPU, then gives the last frame up: the frame before stands in until round_end
(`duplicate` rows in `video_frames.csv`), the files are finished before the capture is torn down, and the verdict is out
at once. On 0.3.1 the GPU readback itself (a D3D `Map`) still never returned on 7 stops, so from 0.3.2 it runs on its own
thread and is abandoned after 2 s. The round is kept (decided 2026-11-09, the client allows a round to end a few seconds
early): the given-up frame usually falls after `round_end` (the stop reaches the machine ~0.1 s later), where the
finalize cuts anyway, and the player is `ok`; when stand-in frames fall inside the round, the player is `degraded` with
the log line above and `odx merge` ends every player's stream where all players still captured (`session.json` →
`end_cut`); over 3 s of stand-in frames the player fails and the room records the round again. The line says which of the
two did not answer and, for the callback, what it was in (`next frame`, `convert`, `readback`); `raw\recording.json` →
`stop.note` and `stop.tail_frames` keep it. Send the log and `recording.json` of such a round when it shows up again.

## No notice after a round

The notices show in the top right corner of the main screen for 6–10 s. If none came, look at the app's status line and
at the orchestrator's log: `round <session>: k/n players kept their recording` says whether a capture failed, and
`<player> is <state>, left out of <session>` names a player still busy with the previous round.

## After a CS2 update

- **The server:** update it (SteamCMD `app_update 730`, or rebuild the hard-link tree), then check the console for
  `[META] Loaded 1 plugin` and that `odx-netlog\*.jsonl` grows while a player is connected.
- **The plugin:** it patches two slots of the netchannel's vtable (`native/cs2-plugin/README.md`). If an update moves them, the
  server crashes when a player connects (a minidump `cs2_*.mdmp` next to `cs2.exe`): rebuild the plugin against an
  updated hl2sdk (branch `cs2`) and check the slots again.
- **The demo reader:** server_state and events come from DemoFile.Net. If a session's `session.json` → `server` has
  `server_state.records` 0 or a `server.capture` warning about reading the demo, update the `DemoFile.Game.Cs` package
  in `dotnet/src/Games/Odx.Games.Cs2/Odx.Games.Cs2.csproj`, rebuild, and run `odx merge` again on the copied round.

## Packets of a player are empty

- They declined the UAC prompt (pktmon needs administrator rights).
- They play on the server's machine and connect to `127.0.0.1`: loopback traffic is invisible to pktmon.
