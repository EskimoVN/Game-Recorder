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

`odx build` finalizes first (`finalized video.mp4: N frames with sound`) and deletes the parts once `video.mp4` stands. A
`video.mp4 has N frames, video_frames.csv M` message means ffmpeg was killed while recording: the video is playable but
short of the last fragment (≤ 4 s), and the round fails validation.

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
