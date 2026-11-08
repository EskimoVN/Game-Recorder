# capture

Tooling for the client's canonical game-data format. See [`../document/software-plan.md`](../document/software-plan.md) for the full plan; this repo currently implements testing layers 1–2 (§7.10).

## What's here

| Module | Purpose | Spec |
|---|---|---|
| `Odx.Core.Geometry` | Engine → RFU axis conversion (RFU/RAGE, Unity, Unreal, Quake/Source), C2W assembly (RUB), roll-free reconstruction, intrinsics from vertical/horizontal FOV or projection | v2 §1.1.2.3–1.1.2.5, appendix §2 |
| `Odx.Core.Frames` | `frames.csv` column list, per-frame input aggregation (held + tapped keys, summed mouse deltas), CSV writer | v2 §1.1.2.6, §1.1.3 |
| `Odx.Capture` / `odx build` | Builds a spec v2 session from a capture folder: `frames.csv` (grid-time frame clock, input aggregation, actions from the game profile's bindings; unbound keys left out) and `session.json` (UTC from `clock_samples.csv`; `session_id` is the round's in a room, else the recorder's `capture_id`, kept as `vendor_capture_id`) | v2 §1.1.2, §1.1.3, §1.5 |
| `profiles/*.json` | Game profiles: `game_title`, `platform`, `localization`, `input_mouse_convention`, `bindings` (key/button → action), optional `window_process` (the game's process: odx-app switches to that window, agents record it when the player picked the monitor) | v2 §1.1.2.1, §1.1.2.6 |
| `Odx.Capture` / `odx validate` | Every vendor check in v2 §1.5 (session.json, frames structure, C2W, intrinsics, input, video read with `Odx.Core.Mp4Reader` — no ffprobe) | v2 §1.5 |
| `config/session_schema.json` | JSON Schema for `session.json`, extracted from the spec; embedded in `Odx.Capture` | v2 §1.1.2.1 |
| `Odx.Protocol` | What the orchestrator and its agents send each other (WebSocket messages, protocol version), `round.json` and the round files; the orchestrator needs nothing else of ours besides `Odx.Core` | v3 §2–§4 |
| `odx-orchestrator` | Multiplayer coordination: rooms, time service, agent hub, game events → per-player (and game server) start/stop, room LANs | v3 §2, §4 |
| `Odx.Agent` | The agents of a player's machine and of the game server (`AgentClient`, `GameServerAgent`), packet capture (pktmon, the elevated helper) and each round's `manifest.json` | v3 §2–§4 |
| `odx merge` / `Odx.Merge` | Assembles each round copied from its participants into the v3 session tree and its sync gate; a game plugs in through `IGameMerger` (its server side) and `IPacketDecoder` (its players' packets) | v3 §2–§4 |
| `odx game-server` | The game server machine's agent: records the server's side of every round (CS2: SourceTV demo, plugin log) and keeps it | v3 §3 |
| `Odx.Games.Cs2` | Everything that knows CS2: `Cs2Server` (RCON, SourceTV, the plugin log; an `IGameServerRecorder` for `odx game-server`) and `Cs2Merger` / `Cs2ServerCapture` / `ServerCamera` (an `IGameMerger` for `odx merge`). A new game is a new `Odx.Games.*` project with these two modules, added in `Odx.Cli` | v3 §3 |
| `Odx.Games.Csgo` / `Odx.Games.Wc3` | Packet decoders (`IPacketDecoder`) for the `csgo` and `w3gs` protocols of a game profile's `network` | v3 §2.3 |
| `tests/Odx.Tests/SessionFixtures.cs` | Synthetic valid session + one mutator per validation rule | plan §7.10.2 |

Everything except the in-game extractor (C++, `native/extractor/`, and the CS2 server plugin, `native/cs2-plugin/`) is C# (.NET 10) in `dotnet/`. The Python pose audit lives outside the product in [`../research/analyze-pose/`](../research/analyze-pose/).

## Capture tools (C#, `dotnet/`)

One command-line tool, `odx` (`src/Apps/Odx.Cli`): `odx input`, `odx record`, `odx build`, `odx validate`, plus `odx simulate-game`, `odx game-server` and `odx merge` for multiplayer (below). Its Windows build has all of them; its `net10.0` build (Linux servers) has `build`, `validate`, `simulate-game`, `game-server` and `merge` only.

`odx input` records keyboard/mouse with Raw Input in its own process (never inside the game) and writes `input_events.csv` + `clock_samples.csv` (QPC ↔ UTC). `InputLogReader` reads the log back; the format is pinned by `dotnet/fixtures/input_events.csv` (writer output must match it byte for byte).

```bash
cd dotnet
dotnet test                                   # .NET 10 SDK; video tests are skipped without ffmpeg/ffprobe on PATH (fixtures; ffprobe checks Mp4Reader)
dotnet run --project src/Apps/Odx.Cli -f net10.0-windows10.0.22621.0 -- input --out C:/odx_capture/session1 [--pid <game_pid>] [--ntp time.windows.com]
```

Language split: plan §6.5.

**Clock (spec v3 §4).** `clock_samples.csv` maps QPC to the Windows clock, which is only as good as its own sync (a test machine here was 44 s behind). `odx input --ntp <server>` also measures QPC against an NTP server (SNTP, RFC 4330) at start and every 15 s into `ntp_samples.csv` (`t_qpc_ms,utc_unix_ms,rtt_ms,system_offset_ms,server`, pinned by `dotnet/fixtures/ntp_samples.csv`); it does not change the Windows clock. `odx build` then takes UTC from the lower-RTT half of those samples (a line fit over ≥ 60 s to absorb QPC rate error, else the median offset), falls back to the Windows clock without them, and records the choice in `session.json` → `vendor_clock_sync` (`source`: `sntp`, `system` or `recording`; `server`, `samples`, `rtt_ms`, `system_clock_offset_ms`). The game server can serve NTP itself so every player measures against the same clock.

**Time server (`odx-capture.json`).** The NTP server is preset per deployment in `odx-capture.json` next to the executables (`bin/` in the package; source: `config/odx-capture.json`), not chosen by users: `{"time_server": "time.windows.com"}` (`host` or `host:port`). odx-app passes it to `odx input --ntp`; `odx input` uses it when `--ntp` is not given; odx-orchestrator measures its own clock against it every minute and records the latest measurement in each round's record, which `odx merge` writes into `session.json` (`server_clock`). Without the file or the field, only the Windows clock is used. In multiplayer, players measure against the orchestrator's time service instead.

**Layer 1: the Windows clock itself (spec v3 §4).** Mapping frames to the orchestrator's clock keeps the players in sync with each other whatever their Windows clocks say, but the spec's baseline is "OS clock via NTP on all machines", so the agent also checks the Windows clock against the real `time_server` on joining a room and on demand (`ClockCheck`, 8 SNTP samples, the lowest-RTT one): more than **50 ms** off (`ClockCheck.MaxOffsetMs`, a proposal, see below) and the player cannot be ready; odx-app's **Sync clock** button runs `odx clock-sync --server <ntp>` as administrator (UAC), which tries `w32tm /resync` and otherwise sets the clock from SNTP itself (the Windows Time service is often disabled on gaming PCs). The check is written to the round folder as `clock_check.json` and into `player.json` → `clock_sync.system_clock`; `welcome` carries the orchestrator's own measurement (`server_clock`). `odx merge` checks `sync.system_clock` for every player over the whole round (NTP − player = (orchestrator − player, the round's samples) + (NTP − orchestrator)) and `session.server_clock` for the orchestrator, both within 50 ms. The orchestrator's SNTP responder keeps answering from its OS clock (on the Linux deployment chrony keeps it right): applying its once-a-minute NTP measurement would add that measurement's jitter to every player's reference.

## In-game camera extractor (C++, `native/extractor/`, Path B)

`odx_extractor.dll` runs inside the game: a Lua profile finds the camera by signature and reads it on every Present. Its toggle key (default F9, read with `GetAsyncKeyState`) starts/stops writing `camera_samples.csv` (QPC time, position, forward/right/up, FOV — in engine axes and units, format pinned by `dotnet/fixtures/camera_samples.csv`) and `extractor.json` (the profile's `convention`, `units_to_world`, `fov_axis`, `camera_model`). It does nothing else: `odx build --camera <that folder>` converts the samples to the canonical C2W + intrinsics and puts them on the video frames (newest sample at or before each frame's capture time, none if older than 100 ms). Input and video come from `odx input` + `odx record` as for any title.

`odx record` captures a game window, or a monitor (the one the game's window is on when the profile names the game, else the primary), with Windows.Graphics.Capture. **Display modes:** windowed and borderless windows record; an exclusive-fullscreen window gives black frames (WGC cannot see it), so run the game borderless, or record the monitor. `odx record --probe` captures the chosen source for up to 2 s without recording and prints a `CaptureProbe` JSON line (frames, black, window style); odx-app runs it before a player is ready. A window that changes size mid-recording (alt-tab out of fullscreen, a mode switch) does not end the recording: its last frame stands in until the size is back (`size_changed_frames` in `recording.json`). Every captured frame carries its QPC time, the same clock as `odx input`. Frames are resampled onto a constant-rate grid and piped to ffmpeg (NVENC when available, else x264; no B-frames). The grid's tick 0 is the first captured frame, or `--grid-origin-qpc-ms`: in a room odx-app puts it on the round's start (`Start.StartUtcMs`, measured against the time service right before the recorder starts: `GridOrigin`, 5 SNTP samples, the lowest RTT, ±RTT/2), so every player's frame k is the same instant; frames before it are discarded, ticks before the first captured frame are black (`leading_black_frames`). While recording, ffmpeg has that one input and writes a fragmented, video-only `video_raw.mp4` (a fragment per keyframe, 4 s at 30 fps: a killed ffmpeg still leaves a playable file minus its last fragment); the sound goes to `audio.wav`, ending at the stop whatever the drain takes. The recorder records and stops, nothing more. Once it has exited, odx-app (`CaptureRun`) finalizes: `VideoFinalize` copies the video stream and encodes the sound to AAC into `video.mp4` (seconds; `recording.json` → `finalize`), cut to the round's frames (`round_frames = ceil((round_end − round_start) × fps)`, the same number on every machine, with `video_frames.csv` cut to match: `recorded_frames`, `tail_frames_cut`), checks the frame count and deletes the two parts. `odx build` does the same when `video.mp4` is missing ([document/record-finalize-plan.md](../document/record-finalize-plan.md), [document/video-alignment-plan.md](../document/video-alignment-plan.md)). Outputs:
- `video.mp4`
- `video_frames.csv` (`frame_index,t_grid_qpc_ms,t_source_qpc_ms,duplicate`; read with `VideoFrameLogReader`, pinned by `dotnet/fixtures/video_frames.csv`)
- `recording.json` (size, fps, start UTC/QPC, `grid_origin_qpc_ms`, duplicated/dropped/starved/black frame counts, `source_frames` (what WGC delivered), `encode_write_ms` / `encode_write_max_ms` (time blocked on ffmpeg's stdin: the encoder behind) and `readback_ms` / `readback_max_ms` (GPU → CPU), `audio`: where the sound came from and how it went, `stop`: how long each phase of stopping took, `ffmpeg_killed` when ffmpeg hit the 20 s limit; added by odx-app: `round_start_utc_ms`, `round_end_utc_ms`, `grid_origin` (`aligned`, or why not), `round_frames`, `finalize`: frames, sound, time and error of the video_raw.mp4 + audio.wav → video.mp4 step)

**Audio** (`--audio`, odx-app's *Record audio*): the game process's own sound through WASAPI process loopback (Windows 10 2004, build 19041, or newer), muxed as an AAC track with sample 0 at video frame 0. When that cannot be had (older Windows, or the activation fails) the default playback device is recorded instead, voice chat and everything else included, and `recording.json` → `audio.status` says so (`source: device`, `fallback_reason`); to keep voice chat out of such a recording, route Discord to another playback device (Settings → System → Sound → App volume and device preferences). `odx record --probe --audio` reports where the sound would come from; odx-app shows it in the room (Audio line) before Ready. Every wait on the stop path has a limit (frame drain 10 s, ffmpeg exit 20 s then kill, audio file 5 s; 31 s in all, under odx-app's 60 s), so a stuck ffmpeg ends in a `recording.json` that says which phase stuck rather than in odx-app killing the recorder and knowing nothing. A Windows call on the stop path that never returns (WGC, D3D or WASAPI teardown) is caught by a 40 s watchdog: the recorder writes `recording.json` and the `stopped` status naming the step (`stop stuck in '…'`), exits (code 0 when every frame had reached ffmpeg), its ffmpeg finishes `video_raw.mp4` on its own and odx-app finalizes it. `video_frames.csv` is flushed per row, so no kill loses rows. The sound never goes through a pipe to ffmpeg: it is muxed from `audio.wav` after the capture.

```bash
dotnet run --project src/Apps/Odx.Cli -f net10.0-windows10.0.22621.0 -- record --out C:/odx_capture/session1 --window Game.exe [--fps 60] [--encoder auto|x264|nvenc] [--seconds 120]
```

Run `odx input` and `odx record` into the same `--out` folder. Grid times (`t_grid_qpc_ms`) are the per-frame clock for `Frames.AggregateInputs`. Requires Windows 10 2004+ and ffmpeg (the packaged `ffmpeg/ffmpeg.exe`, else PATH, or `--ffmpeg`). `--encoder auto` (default) uses NVENC when it works on the machine, which keeps the CPU free for the game, else x264; the probe (~2 s cold) is cached per ffmpeg build in `%LOCALAPPDATA%\Odx\encoder.json`. NVENC needs an NVIDIA GPU with driver 471.41+ (the packaged ffmpeg is built with NVENC 11.1 headers; ffmpeg builds with the newest headers need 610+). Both commands also accept `--stop-event <name>`: they stop when that named Win32 event is set.

`odx-app` (WPF, `src/Apps/Odx.App`) is the desktop front end. It runs `odx input` and `odx record` as child processes into the package's `data/captures/<yyyyMMdd-HHmmss>/` (in a room: `data/captures/<session>/`) and stops both through `--stop-event`. The package keeps its data next to `bin/` (`PackageFolders`): `data/captures/` (recordings made on that PC), `data/delivery/` (the sessions `odx merge` builds, what to hand over) and `data/logs/` (odx-app's daily log, `odx-app-<yyyyMMdd>.log`, kept 14 days: everything the agent reports and each round's full result, what to ask a player for when a round went wrong); rebuilding the package keeps `data/`, and the zip carries only empty `data/captures/` and `data/delivery/`.
- English / Vietnamese UI (all texts in `src/Apps/Odx.App/Strings.cs`; defaults to the Windows display language, switchable at any time). Validator issue messages stay in English.
- Source (primary monitor or a window), FPS and *Record audio* (on by default); in a room the game profile and FPS rows are hidden (the orchestrator sets them); the OBS rows show only when `record_with_obs` is already on in the saved settings; the time server comes from `odx-capture.json` (the result shows the measured RTT and Windows clock offset and warns above 1 s); settings saved in `%APPDATA%\Odx\odx-app.json`.
- Global start/stop hotkey, default `F10`; click the hotkey box and press a new combination to change it. While the app runs, other programs (and the game) do not receive that key. It is still recorded in `input_events.csv`; `odx build` leaves it out of `input_keys` as long as the game profile does not bind it.
- Start shows a 3-second countdown overlay (click-through, never takes focus); capture begins after it closes, so it is not in the video. Start/hotkey again during the countdown cancels. Stopping shows an "END" badge once both capture tools have exited (also never in the video), then a notice with the result in the top right corner of the screen for a few seconds (click-through, never in the video; Windows' own notifications are hidden while a game runs full screen). In a room the notice says where the round was kept (or that its capture failed).
- One instance only: starting it again brings the running window back from the tray.
- Minimizing hides it to the tray; the hotkey keeps working.

```bash
dotnet run --project src/Apps/Odx.App
```

## Package for other machines

```powershell
cd dotnet
powershell -ExecutionPolicy Bypass -File publish.ps1 -Version 0.1.0
```

Produces `dotnet/dist/odx-capture-<version>-win-x64.zip` (~140 MB, 8 files). Building it needs MSYS2 (ffmpeg, below) and the MSVC build tools (NativeAOT launcher). Unzip it on any Windows 10 2004+ x64 machine and run `Odx Capture.exe`; no .NET or ffmpeg install is needed.

```
odx-capture-<version>-win-x64/
  Odx Capture.exe          launcher (NativeAOT, 2 MB): shows the splash at once, starts bin\odx-app.exe
  bin/
    odx-app.exe               desktop front end
    odx.exe                   command-line tool (input, record, build, validate)
    ffmpeg/                   ffmpeg.exe (6 MB, built by ffmpeg/build-ffmpeg.sh), LICENSE.txt, SOURCES.txt
    games/                    game profiles
```

**Start-up.** Windows Defender scans a new executable before it runs: for the 240 MB `odx-app.exe` that took 12–27 s on its first launch after unzipping, with nothing on screen. The launcher is scanned at once and shows the splash (the same image odx-app's own WPF splash shows next, so the hand-over is invisible); it closes when odx-app shows a window. Measured: splash after 0.1–0.2 s and main window after ~1.5 s on later launches; on the very first launch of a freshly unzipped package Defender still held even the launcher 6–9 s. Code-signing the executables is the remaining fix for that.

Both odx executables are self-contained single files: each embeds its own .NET runtime (WPF's native libraries are extracted to a temp folder on first run), so they never share or overwrite each other's files. They are not compressed: decompressing on every start made each launch 0.7–1.3 s slower, and the zip is compressed anyway.

odx record uses `ffmpeg/ffmpeg.exe` from the package when present, otherwise `ffmpeg` on PATH; nothing needs ffprobe.

**ffmpeg build.** `dotnet/ffmpeg/build-ffmpeg.sh` builds a static ffmpeg with only what `odx record` uses — rawvideo from stdin, crop/scale/format, libx264 and h264_nvenc, MP4 to a file — from pinned, SHA-256-checked sources (FFmpeg 9.0.2, x264 stable, nv-codec-headers 11.1.5.4): 6 MB instead of ~100 MB for a general build. `publish.ps1` runs it once in MSYS2 (`C:\msys64` or `MSYS2_ROOT`; `pacman -S make diffutils mingw-w64-ucrt-x86_64-gcc mingw-w64-ucrt-x86_64-nasm mingw-w64-ucrt-x86_64-pkgconf`), ~10–15 min, and caches it in `.cache/ffmpeg-<script hash>/` so changing the script rebuilds it. The result is GPLv2 (FFmpeg `--enable-gpl` with x264); `LICENSE.txt` and `SOURCES.txt` (exact sources and configure line, for the GPL source offer) ship next to it. Our tools only run it as a separate process (not linked).

## Versions and CI

Every tool stamps `<version>+<git commit>` into its output: `tool_version` in `recording.json`, `vendor_tool_version` in `session.json` (an extra field; the schema allows additional properties). The .NET SDK is pinned by `dotnet/global.json`. CI (`.github/workflows/ci.yml`, Windows) builds and tests `dotnet/`, then the extractor with ctest.

## Build and validate a session

`odx-app` does both automatically after each recording when a game profile is selected. By hand:

```bash
cd dotnet
dotnet run --project src/Apps/Odx.Cli -f net10.0 -- build path/to/<capture-dir> --profile ../profiles/example-fps.json
dotnet run --project src/Apps/Odx.Cli -f net10.0 -- validate path/to/<capture-dir> [more dirs...] [--json] [--no-video]
```

Exit code 1 when any error is found. Warnings (`c2w.roll`, `c2w.upright`) do not fail a session.

## Multiplayer (spec v3)

Step-by-step setup for a test with players on several machines (CS2 server, orchestrator, network, player PCs): [`guides/`](guides/README.md).

Planned restructuring (four apps, local recordings merged per game, upload later): [`PLAN.md`](PLAN.md).

Three parts, each its own process (ASP.NET Core or .NET, cross-platform), so the game server, the coordination and the heavy assembly never share a CPU unless you put them on one machine:

| Process | Where | Does |
|---|---|---|
| `odx-orchestrator` (`src/Apps/Odx.Orchestrator`) | one machine everybody can reach | rooms, rounds (arm / start / stop every capture), the time service, room LANs; no files |
| `odx game-server` (`src/Apps/Odx.Cli`) | the game server's machine | records the server's side of every round and keeps it in its `--work` folder |
| `odx merge` (`src/Apps/Odx.Cli`) | any machine with the rounds copied to it; disk for every player's video | builds every round of a folder into a session, one at a time |

```bash
cd dotnet
dotnet run --project src/Apps/Odx.Orchestrator -- --game-profile ../profiles/example-fps.json [--http-port 5180] [--time-port 12300] [--fps 30] [--access-key K]
dotnet run --project src/Apps/Odx.Cli -f net10.0 -- merge <test> [--out DIR]
```

Each participant keeps its own part of a round; for now the tester copies them by hand into one folder per round (upload to a server is a later step):

```
<test>/<session>/
  round.json  server_events.jsonl  game_profile.json     (from any participant: all got the same; a player's are in raw\)
  players/<pid>/                                          (each player's data\captures\<session>\)
  server/                                                 (the game server's <work>\<session>\server\)
```

`odx merge <test>` builds every session folder in it (a folder with `round.json`; `<test>` itself may be one) into `<out>/odx/<mm-dd-yyyy>/<game>/<session>/` (default out: the package's `data\delivery\`), prints PASS/FAIL per session with its issues, and exits 1 if any session fails (2 on bad usage or when there is no session).

**Rooms.** Players are recorded only with the other members of their room. Set the orchestrator's address once in `odx-capture.json` (`"orchestrator": "http://<host>:5180"`; empty: odx-app records on its own only, the room rows stay hidden). In odx-app:

- **Create room** opens a room with you as its host and shows its 6-character invite code (no 0/O or 1/I) to share. **Close room** ends it for everyone; a room nobody is in also closes after 30 min.
- **LAN (ZeroTier)** (`AgentOptions.Lan` when creating a room; no longer offered in the window, the games tested play over the internet) gives the room its own virtual LAN for games that only play over LAN (e.g. Warcraft III Classic/1.29): everyone in the room is on one layer-2 network, so LAN game lists and broadcasts work. The orchestrator creates a ZeroTier network per room on its controller (`--zerotier-api`, token from `--zerotier-token-file` or `ODX_ZT_TOKEN`), authorizes each member's ZeroTier node while they are in the room and deletes the network when the room closes; odx-app joins and leaves it through the local ZeroTier service (the per-user token copy that ZeroTier's tray app keeps, so no admin rights) and shows its LAN IP. Without ZeroTier, odx-app offers to install it (winget, one UAC prompt).
- **Join room** asks for the invite code in a dialog; **Leave room** to go. Leaving during a countdown cancels the round; leaving during a round stops your recording.
- In a room, the main button and the hotkey are the room action:
  - the host types the round's **task ID** (its session id: the folder every player's capture and the delivered session get; letters, digits, `_ . -`; typed again, it is a new take of that round and every machine moves its earlier folder to `<id>.replaced-<time>`) and starts and ends rounds (3 s countdown);
  - the other players say they are ready (again to take it back); the host can start only once all of them are, and once nobody (the host included) is still saving the last round (the button waits, the orchestrator checks too). 

  The status line lists the members (✓ = ready). The game profile and FPS come from the orchestrator.

Rounds start and end only on request: the host's, or a game server plugin's `round_start` / `round_end` in a room without a host (`POST /rooms`). Ready is only a signal for the host; it never starts a round by itself.

Every member starts capturing when the countdown starts and sees it on screen (excluded from screen capture, so never in the video). A `round_end`, an unready, or a member leaving during the countdown cancels the round (`round_cancelled`): everyone stops, the players' folders are renamed `<session>.cancelled-<yyyyMMdd-HHmmss>`, the game server's is deleted, and the round number (and session id) is used again. After a lost connection an agent joins its room again. A connection lost during a round (not its countdown) does not stop the recording: the round keeps that agent for `--reconnect-grace-s` (30 s), sends it any Stop it missed when it is back, and takes its `done` again (agents resend it until the round's files arrive). Still gone after the grace, the player is stopped at that moment on both sides; a room's LAN keeps its node for the grace too (the agent says its node again on every connection). An agent back after its round finished gets the round's files anyway, from the orchestrator's state, when it sends its `done` again. Both sides send WebSocket PING/PONG every 5 s, so a dead connection (Wi-Fi gone) is noticed within about 15 s.

Headless capture machines (odx-app without a window, as many per machine as wanted; the orchestrator and access key default to `odx-capture.json`, `--out` to `data/captures/`) and the simulator (in place of a game server plugin while there is none):

```bash
odx-app --headless --room <code> --player p1 [--auto-ready] [--name Alice] [--window cs2] [--out C:/odx_capture] [--orchestrator http://<host>:5180] [--access-key K]
odx simulate-game --orchestrator http://<host>:5180 --players p1,p2 [--room <code>] [--rounds 2] [--round-s 20] [--match sim]
```

`simulate-game` opens a room without a host (or uses `--room`), prints its invite code, waits until every `--players` member has joined, then plays scripted rounds: each has a kill and a permanent death halfway through. Between rounds it waits until no round is pending and every member is idle; it prints `<session>: k/n players kept their recording` per round and exits 1 if any player did not keep theirs (`odx merge` gives the gate). Run on one machine (orchestrator + two headless odx-app recording the monitor + simulator), copied and merged, two 12 s rounds pass; first frames arrive 0.75–0.93 s after `round_start`.

- **Time service:** an SNTP responder on UDP `--time-port`. Every player's capture measures QPC against it (`ntp_samples.csv`), so all players share one clock.
- **Agents:** each player's machine runs `AgentClient` (`Odx.Agent`). It connects to `ws://<host>:5180/agents`, measures its offset on `welcome`, creates or joins a room (`create_room`, `join_room`; the orchestrator answers with `room` updates), starts its capture on `start` into `<out>/<session>/` and stops on `stop`. Then it builds and validates the capture, keeps the folder and reports `done`. The folder keeps what is delivered on top, laid out as the delivered player folder `AMRF-100-S01` (2026-10-27), itself a player folder of the client's multiplayer delivery (sample round `20261017-100551-2907ed-r03`) plus `session.json` and `key_bindings.json`: `video.mp4`; `player.json` (the spec v2 session fields as the sample writes them: vendor `Odx`, `created_at_utc` / `ended_at_utc` to the second, whole `duration_ms`, no `vendor_*` extras; plus `player`, `network.ping_ms`, `clock_sync` (offset = server − player clock, RTT) and `recording.video_first_frame_utc`; `PlayerFile`); `frames.csv`; `frames_bin.bin` (the client's `FrameRecord` after a `FileHeader` whose magic and record type are `frames_bin_magic` / `frames_bin_record_type` of the deployment config: `frames.csv` at full precision plus `server_time_us` and the vertical FOV; `ClientFramesBin`, `client_mp.proto`); `events.jsonl` (the round's server events in the sample's field order, without `utc_ms`, with this player's `video_time_ms`, `server_time_as_local_us` and, from the `game_event` notices the orchestrator sends each member as it stamps an event, `local_receive_utc` / `local_receive_time_us`); `key_bindings.json` (the game profile used, `bindings` included: SOW §2 asks for key bindings); and the spec v2 `session.json`. Every raw log is in `raw/` (`CaptureFolder`), where the round's files also go, with `manifest.json` (every file's path, size and SHA-256). odx-app keeps the last round's outcome in the room status: kept (and where) or capture failed.
- **Game events:** a game server plugin opens a room without a host (`POST /rooms[?server=<id>]` → `{"code"}`), lets the players join it, and posts JSON events to `POST /rooms/{code}/events`; `GET /rooms/{code}` and `GET /status` show the rooms (`GameEvents`: `match_start`, `round_start`, `round_end`, `kill`, `player_death` with `permanent`, `player_connect`, `player_disconnect`, `match_end`). Events without `utc_ms` are stamped on arrival.
  - `round_start` arms and starts every idle agent at once. With `countdown_ms` it is recorded as `round_countdown` and the orchestrator adds `round_start` when play starts, so the video holds the start of play (`round_start_frame` in `player.json`). Players' starts differ by their capture start-up (~0.8 s warm; ~3 s on the first start after an install, while Windows Defender scans the new files); every frame is put on server time afterwards. Events between rounds (`match_start`, `player_ready`, `round_cancelled`) open the next round's events.
  - A permanent `player_death` stops that player.
  - `round_end` stops the rest.
- **Round files:** once every player and the game server agent are done (or 15 min later) the orchestrator sends each participant `round_record` with the round's files: `round.json` (the final record), `server_events.jsonl` and `game_profile.json`; each writes them into its folder of that round. `GET /rooms/{code}` lists the finished rounds under `sessions` (`session_id`, `players`, `kept`).
- **Assembly:** `odx merge` builds each copied round with `MultiplayerSession.Assemble`; the game server's side is read by the game's module (`IGameMerger`, chosen by the profile's `game_title`; CS2: `Cs2Merger`), and a game without one gets an empty `server/`. It writes:

  ```
  <out: data/delivery by default>/<vendor>/<mm-dd-yyyy>/<game>/<session-id>/
    session.json  validation.json  schema.bin.gz  server/events.jsonl
    server/ server_state.bin packets.bin packets.txt   (with a game server attached: CS2, below)
    players/<pid>/ player.json frames.csv frames.bin video.mp4 events.jsonl key_bindings.json [packets.bin packets.txt]
  <out>/_work/<session-id>/ players/<pid>/ (v2 session.json, raw logs, packets.pcapng) server/ (the game server's own recording)
  ```

  - `session.json`: `spec_version` 3.1, `round_index`, `countdown_ms`, `end_reason`, `game_engine` and `coordinate_system` (from the game profile), `rules` (start/stop trigger, recording, respawn), the players (id, name, machine id, Steam id, tool version, stop reason, validity), the orchestrator's own NTP measurement (`server_clock`), the game server's side (`server`: build, map, tick rate, ticks, sources), and how to decode the `.bin` files.
  - `player.json`: ping, `network`, `clock_sync` (offset = server − player clock, RTT, jitter, drift, every sample), `files`, first/last frame UTC, `round_start_frame` / `round_start_video_time_ms`, recording details (fps, sizes, encoder, capture source, duplicated/dropped frames), frame stats, key bindings, stop reason, and `qa` (every check with its result).
  - `frames.bin` (spec v3 §2.2): `frames.csv` at full precision plus each frame's `server_time_us`, as length-delimited protobuf `odx.capture.v1.FrameRecord` after a `FileHeader`; `schema.bin.gz` is the schema (`capture.proto`) as a gzip-compressed `FileDescriptorSet`.
  - Every server event carries `event_id`, `utc`, `server_time_us`, `session_id` and `round_index`. A player's `events.jsonl` holds every event of the round with its `video_time_ms` in that player's video (negative before the first frame) and the `frame_index` on screen at that moment (null outside the video).
  - `packets.bin` / `packets.txt` (spec v3 §2.3), for games whose profile has a `network` section: each player's game traffic of the round, captured by pktmon (built into Windows; it needs administrator rights, so odx-app starts `odx packet-capture` with a UAC prompt the first time a room wants it, keeps it until the app closes (a declined prompt is not repeated before then) and drives it over a named pipe only that user can open; `odx-app --headless` uses pktmon directly when run as administrator). The capture covers the profile's ports on the network adapters (Ethernet and Wi-Fi/802.11 frames) and goes into `capture/packets.pcapng`. Records are `odx.capture.v1.PacketRecord` (server and local time, direction from this machine's addresses, endpoints, message, bytes, decoded fields); `packets.txt` lists them tab-separated.
    - `w3gs` (Warcraft III 1.26–1.29, `profiles/warcraft3.json`): TCP streams are reassembled (retransmissions skipped, gaps listed as `TCP_GAP`) and split into W3GS messages, every one decoded: LAN discovery (`GAMEINFO` with map and host from the encoded stat string), joining and slots, chat, lag, map download, and the lockstep command blocks action by action (orders with order id and coordinates, selections, groups, items, resources, pings…); unknown messages or actions keep their bytes as hex.
    - `csgo` (CS:GO legacy 1.38.8.1, `profiles/csgo.json`): every UDP datagram of the game server port goes through the netchannel layers: connectionless packets (A2S_INFO, challenge, connect; a server's `S2A_INFO` also tells its build, which switches the key to it), split datagrams reassembled, ICE-2 decryption with the key derived from `network.version` (the build), unpadding, LZSS/Snappy decompression, the netchannel header (sequence, ack, flags, choked), reliable subchannel transfers (fragments across datagrams, compression, files) and the netmessages, named `svc_*` from the server and `clc_*` from the client (`net_*` both ways) after netmessages.proto. Message payloads are decoded by protobuf field number (`fields`), not yet by name. ICE is ported from the author's public-domain reference and passes its certification triplets.
    - `raw` (CS2, `profiles/cs2.json`): one record per UDP/TCP payload. CS2 encrypts every connection with its own AES-GCM key (GameNetworkingSockets), so nobody can decode it passively; its decoded game messages come from our own server (`server/packets.*`, below).
  - **Deliver the vendor tree only** (`<out>/<vendor>/…`): it holds exactly spec v3 §1's files plus `key_bindings.json` (SOW §2), `validation.json` and `schema.bin.gz`; `odx merge` fails a session (`delivery.files`) if anything else is in it. The working files (the v2 `session.json`, raw logs, `packets.pcapng`, the game server's recording) go to `<out>/_work/<session>/` beside it. The agents' round folders are working copies, never a delivery.
  - **Pre-roll cut (`video.trimmed`):** players record from the countdown, so each delivered stream is cut to its first frame at or after `round_start`: the video is re-encoded for a frame-accurate cut (x264 crf 18, 1–2 min per player of 1080p60 on the merge machine), `frames.csv` restarts at frame 0 / 0 ms, `frames.bin` and `events.jsonl` follow; `player.json` keeps `pre_roll_frames` / `pre_roll_ms`. A cut that cannot be made (ffmpeg missing, counts off) fails the player rather than delivering mixed streams. Frame counts are checked by decoding (`video.decoded_frame_count`, `odx validate --decode`), not by the MP4 sample table alone: a recorder's cut-short last sample counts there but does not decode.
- **CS2 game server (spec v3 §3):** `odx game-server` (`GameServerAgent`), on the CS2 dedicated server's machine, connects to the orchestrator like an agent (role `game_server`, no room) and records the server's side of every round:

  ```bash
  odx game-server --orchestrator http://<host>:5180 --cs2-game-dir <cs2-server>/game/csgo \
      --cs2-rcon-password-file <file> [--cs2-rcon <address>:27015] [--work DIR] [--access-key K]   # or ODX_CS2_RCON_PASSWORD
  ```

  CS2 binds RCON to the machine's network address, not to 127.0.0.1; without `--cs2-rcon` the agent uses the first IPv4 address of a network interface that is up. While a round runs it samples its machine's clock against the orchestrator's time service every second (`server/ntp_samples.csv`); `odx merge` puts the plugin's and the agent's times on the orchestrator's clock with the median offset over the lower-RTT half of them (`session.json` → `server.clock_sync`). Without samples (the agent on the orchestrator's machine) the offset is 0. `GET /status` lists the connected game server agents (`game_servers`). Several can share one orchestrator, each with its own `--id`; a room opened with `POST /rooms?server=<id>` is recorded by that one (else by the only one connected).

  - The server: its own install (never a player's CS2), Metamod:Source 2.0 and the `odx_netlog` plugin (`native/cs2-plugin/README.md`), started with `-dedicated -usercon +rcon_password <password>`, and `tv_enable 1`, `tv_delay 0`, `tv_autorecord 0` in `game/csgo/cfg/gamemode_<mode>_server.cfg` (the stock gamemode configs set a 30–105 s SourceTV delay, which would cut the end of every round).
  - When a round's countdown starts the agent sends `tv_record replays/<session>` over RCON, and `tv_stoprecord` when the round ends (a cancelled round's demo is deleted). CS2 counts SourceTV as a bot: a match restart or a bot change can kick it, and it only comes back with a map load (`map <map>`; `changelevel` to the same map only restarts the match). The agent checks that the demo appears after `tv_record`; if not, it logs an error and the session gets a `server.capture` warning (`session.json` → `server.problems`). The demo, the lines the plugin logged meanwhile and the clock samples are kept in `<work>/<session>/server/`, with the round's files next to it once `round_record` arrives; the orchestrator sends those only after the agent's `done`.
  - `server/server_state.bin`: `odx.capture.v1.ServerStateRecord`, one per server tick, read from the SourceTV demo (DemoFile.Net): every player's session `player_id`, Steam id, name, slot, team, alive, health, armor, position, eye position and angles, field of view, velocity, active weapon, ducking/scoped/walking, ping, kills/deaths/assists, connected, bot; grenades in flight, the bomb carried/dropped/planted (site, defusing); the game's phase (warmup, freeze, live, over), round start, length and time left, bomb planted, rounds played. `steam_id` matches `player.json` (the player's agent reads the signed-in Steam account; one shared by several players maps to none).
  - **Camera per frame from the server:** CS2's client cannot be read (anti-cheat), so a player whose capture has no camera gets it from the server: the eye position and view angles at frame time + half the ping (the server applies a view about that long after the screen shows it), ticks interpolated, as a spec v2 C2W (the game's world is already right-handed Z-up) and PINHOLE intrinsics from CS2's field of view (horizontal at 4:3: 90°, less in a scope). Frames while the player is dead (someone else's view) get none and are listed in `player.json` → `frame_cleanliness.not_first_person_frame_ranges`; `player.json` → `camera` describes the source. `sync.input_camera` (advisory) checks the result against the player's own mouse: the camera's yaw change must follow `input_mouse_dx` in the same frame (on a real round: correlation 0.97).
  - `server/events.jsonl` adds the game's own events from the demo, all of them, decoded by the game's event list (name and key names of each of the ~270 events): `{"type": "game_event", "name": "player_death", "userid": {slot, player_id, steam_id, name, position}, "attacker": {…}, "weapon": "hkp2000", "headshot": true, …, "utc_ms", "tick", "source": "demo"}`. Player keys become the player (with where they were); a pawn key that repeats one is dropped. Per-shot, per-step and UI/SourceTV events (`weapon_fire`, `player_footstep`, `hltv_*`…) are left out (spec v3 §3.2: events that pace the game; their effects are in `server_state.bin`). CS2 does not send `round_start` / `round_freeze_end` / `round_end` to SourceTV; they come from the game rules entity (`"from": "game_rules"`). A demo recorded mid-map does not always carry the event list; the plugin's signon data supplies it (`session.json` → `server.game_event_names` says which). Players' `events.jsonl` get them with their video time.
  - `server/packets.*`: every net message the server exchanged with each client (`peer_id`: the client's Steam id, `peer_player_id` and the `peer` column of `packets.txt`: their player id), decoded by the engine and converted from protobuf text to JSON, plus the signon data sent during the round (split from the buffers the plugin saved and decoded with the game's protobuf classes, via DemoFile).
  - Ticks go on the clock from the plugin log: each line carries the server tick it was written in, and the earliest line of each tick gives its time (interpolated between; 64 ticks/s beyond). Without a plugin log the demo's first tick is the moment `tv_record` was sent.
  - `_work/<session>/server/` keeps the demo and the plugin log as recorded (next to the delivery, not in it).
  - Not yet: CS2 deaths do not stop a player's recording during the round (nothing posts a live `player_death` yet); everyone records until the round ends.
- **Gate** (`validation.json`, per-player checks in `player.json` → `qa`; `GateTolerances`):
  - each player passes v2 §1.5;
  - floors: ≥ 30 fps, ≥ 1280x720 (spec v3 §2), ≥ 10 s (not for a player stopped by a permanent death), camera on ≥ 90 % of frames when there is camera data;
  - `sync.clock`: median RTT ≤ 100 ms;
  - `sync.system_clock`: the player's Windows clock within 50 ms of NTP during the round (via the orchestrator's own NTP measurement); `session.server_clock`: the orchestrator's within 50 ms too;
  - `sync.start`: first frame ≤ 3 s after `round_start` + `countdown_ms`;
  - `sync.end`: last frame ≥ stop − 1 s;
  - `capture.window` (advisory): only the game window is captured;
  - the session needs K of N valid players (K = N up to 3 players, else N − 1).

## Deploy the orchestrator (Linux server)

`deploy/` runs odx-orchestrator in Docker behind Caddy, which gets the HTTPS certificate for your domain from Let's Encrypt and passes everything, the agents' WebSocket included, to the orchestrator. The CS2 server runs on its own Windows machine with `odx game-server` ([guides/cs2-server.md](guides/cs2-server.md)).

1. On the server: Docker with the compose plugin; the domain's DNS A record pointing at the server (directly, not behind a proxy such as Cloudflare's orange cloud: players also reach the time service at `<domain>:12300/udp`); ports 80/tcp, 443/tcp+udp, 12300/udp and 9993/udp (ZeroTier controller for LAN rooms) open. Never publish 9993/tcp: that is the controller's API, reached only by the orchestrator inside the compose network.
2. Copy `capture/` there (`git clone`, or `rsync -a --exclude bin --exclude obj --exclude dist --exclude .cache capture/ server:capture/`).
3. `bash capture/deploy/up.sh`. The first run writes `deploy/.env` with a random `ODX_ACCESS_KEY` (and later the ZeroTier controller's `ODX_ZT_TOKEN`); set `ODX_DOMAIN` (and `ODX_GAME_PROFILE`, a file of `profiles/`) in it and run it again. It builds the image, starts orchestrator + ZeroTier controller + Caddy (restarting on reboot), and prints the address and the key. Run it again after an update.
4. Package odx-app for that server on Windows: `powershell -ExecutionPolicy Bypass -File publish.ps1 -Orchestrator https://<domain> -AccessKey <key>` writes both into the package's `bin/odx-capture.json`, so players only type invite codes. Or let GitHub build it: the workflow "Release capture package" (`.github/workflows/release-capture.yml`, tag `capture-v<version>`) runs the same with the repository variable `ODX_ORCHESTRATOR` and secret `ODX_ACCESS_KEY` and attaches the zip to a release. On AWS, `deploy/aws/create-orchestrator.sh` sets up the server ([deploy/aws/README.md](deploy/aws/README.md)).

The server stores no recordings: the rounds stay with the players and the game server agent until copied for `odx merge`. Its state (rooms, rounds, the log of every round; SQLite) is in `deploy/state/orchestrator.db` and survives restarts and updates. Logs: `docker compose -f deploy/compose.yaml logs -f orchestrator`. Every request needs the access key (`Authorization: Bearer <key>`, 401 otherwise); the time service is open (it only tells the time). A game server plugin calls `POST /rooms` and `POST /rooms/{code}/events` with the same key.

## Decisions pending with the client

- **Null encoding in CSV:** nulls are written as the literal `null` (`Frames.Null`), as spec v2 §1.5.5 asks ("null, not empty string") and as the client's multiplayer sample delivery does; confirm.
- **Column-name case:** the spec uses `c2w_mRC` (§1.1.2.4, §1.5.3) but `C2W_M00` in the summary table (§1.1.3). Uppercase `C2W_M00` is used here, as in the summary table and the client's sample delivery.
- **Player folder as in the client's multiplayer sample (decided 2026-10-24):** a player's round folder follows the sample round `20261017-100551-2907ed-r03` instead of spec v2 where they differ: `frames.csv` has no distortion columns (spec v2 §1.1.3 and §1.5.2 want every header present, null when unused), `camera_model` is `pinhole` (§1.5.4 spells `PINHOLE`; the validator accepts either case), the spec v2 `session.json` sits next to `player.json`, which repeats its fields. The folder `AMRF-100-S01` (2026-10-27) names the binary frames file `frames_bin.bin`, where spec v3 §1 names it `frames.bin`; the player folder follows it (`odx merge` still writes `frames.bin`). No schema came with the sample's `frames.bin`: `client_mp.proto` is read off its bytes (fields 12 and 13 never occur there); ask the client for theirs.
- **Extra `session.json` fields:** `vendor_tool_version` and `vendor_clock_sync` are not in the spec (the schema allows additional properties); confirm the client accepts them.
- **Validation tolerances** not fixed by the spec (all in `ValidationTolerances`, `Odx.Capture/Validation/Issue.cs`): rotation `1e-3`, principal point `4 px`, frame spacing `max(1 ms, 25%)`, level-roll warning `0.05`, fx = fy within `1e-3`, duration checks `1 s` / `1000 ms` / `2 frames`.
- **First- and third-person views (Arma Reforger, Arma 3):** the client wants both (2026-10-18). Spec v2 has no field for the
  view, and a third-person C2W is a camera behind the character, not the player's eyes. Proposal: a per-frame column
  `camera_view` (`first_person` / `third_person`) in `frames.csv` and the views used listed in `session.json`, rather
  than one session per view (players switch mid-round); confirm the encoding and whether both count toward the hours.
  Arma 3 also has `GUNNER` (looking through optics or a vehicle sight) and `GROUP` (commander) views: map `GUNNER` to
  first person, block or flag `GROUP`?
- **Multiplayer (provisional):**
  - One round = one session, named by the host (the task ID typed before the round; letters, digits, `_ . -`; a name typed again replaces the earlier round of that name, kept aside as `<name>.replaced-<time>`) or, in a room driven by a game server plugin, `<match>-rNN`.
  - Ping is the median NTP RTT to the orchestrator.
  - **`key_bindings.json` stays in the delivered player folder** (SOW §2 asks for key bindings) although spec v2.1 dropped the file in favour of `input_keys` / `input_actions` in `frames.csv` (which are there too): confirm whether to keep it.
  - **Pre-roll cut by re-encoding:** the delivered video is re-encoded (x264 crf 18) to start exactly at `round_start`; the original countdown recording stays in `_work/`. Confirm that a second encoding generation is acceptable, or that frame 0 may instead stay at the countdown with `round_start_frame` pointing at play.
  - **Audio fallback:** when the game's own sound cannot be singled out (Windows 10 before 2004, build 19041, or the process loopback fails) the whole playback device is recorded instead, voice chat included, and `player.json` → `recording.audio` says so (`source: device`, `fallback_reason`). Confirm whether such rounds count, or whether only `source: process` is accepted.
  - **Packets stay required** (spec v3 §2.3): a player without a usable `packets.pcapng` fails `packets.captured`, and odx-app refuses Ready when the administrator prompt was declined. If the client drops the packet requirement for a game, remove `network` from its profile: no capture, no prompt, no check.
  - **Clock tolerance (v3 §4 layer 1): 50 ms** between any machine's Windows clock and NTP (`ClockCheck.MaxOffsetMs`): one frame at 30 fps is 33 ms and public NTP over the internet is good to ~10 ms, so a clock that far off was not synced at all. The spec says "more prototyping is needed to decide what a good tolerance is": confirm the figure. The player cannot be ready beyond it (odx-app offers to sync the clock); `odx merge` fails the player (and the session when the orchestrator is off).
  - The sync tolerances above (`GateTolerances`) and the K-of-N rule are proposals (v3 §4 leaves them open); the 10 s and 90 % camera floors come from the client's sample delivery, not the spec.
  - The 10 s floor does not apply to a player stopped by a permanent death: v3 §1 asks to cut that recording, so a player who dies early would otherwise always fail.
  - Agents still busy (recording or building) with the previous round are left out of the next one.
  - The game server's machine clock is put on the orchestrator's with one offset per round (median over the lower-RTT half of its SNTP samples, sampled once a second during the round), not a drift fit: over a round of minutes two synced Windows clocks drift well under one 15.6 ms tick. Confirm this is precise enough for spec v3 §4 or ask for a fit.
  - Rooms: the player who creates a room is its only host; only the host (or a game server plugin) starts and ends rounds, once every other member is ready. There is no per-player stop without a game server, so everyone records the whole round. Match ids start with the room's code; a cancelled round's number is used again.
  - `player.json` / `session.json` layout: ours is flat where the client's sample delivery groups fields (`player`, `game`, `network`, `files`, `camera`); we added the groups that carry information we have (`files`, `network`, `camera`, `frame_cleanliness`, `rules`, `coordinate_system`) next to the existing keys. Confirm whether the client wants the sample's exact layout.
  - Player packets: Warcraft III decoded (field layouts from BNETDocs/gowarcraft3) and CS:GO legacy decoded down to netmessages (layout from Synacktiv's protocol notes and open Source engine client emulators; netmessage contents by field number for now), neither yet checked against a real game here; CS2 kept raw (encrypted per connection). Server side: CS2's `server_state.bin` / `server/packets.*` come from our own dedicated server (SourceTV demo + plugin log); the `ServerStateRecord` layout is our proposal (v3 §3.1 leaves the binary format to us, with extraction instructions: `capture.proto` and `schema.bin.gz`). Warcraft III has no server, its host's capture is the authoritative side; CS:GO legacy has none yet.
  - CS2 player packets (Q12): they are encrypted per connection, so they hold only sizes and timings, while the server's `server/packets.*` (odx_netlog) hold the decoded messages. If the client accepts the server side alone for CS2, `network` comes out of `profiles/cs2.json` and CS2 players get no UAC prompt at all. Games without a server log of ours (CS:GO legacy, Warcraft III) keep the player capture.
