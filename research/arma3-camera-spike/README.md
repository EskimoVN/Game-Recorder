# Arma 3 capture: launcher and camera addon

The player side of Arma 3 capture (document/games/arma3.md §3, §5), the counterpart of `research/reforger-camera-spike`:

- `capture/native/arma3-mod/odx_capture/`: the client addon. `ODX_fnc_cameraLog` (postInit) adds a `Draw3D` handler that
  writes the render camera every rendered frame to the game's `.rpt` through `diag_log`, prefixed `ODXCAM,`: the UTC time
  to the millisecond (`systemTimeUTC`, kept as date parts because SQF numbers are 32-bit floats), the view (`cameraView`),
  the camera's right/up/forward in world axes and its ASL position (`positionCameraToWorld`, `AGLToASL`), and the vertical
  FOV measured from where points 0.5° above and below the view axis land on screen (zoom and the profile FOV included).
  Raw log only (working rule 3): odx-app's `Odx.Capture/Arma3Camera` reads the `.rpt` after a round, keeps the rows within
  the round's clock samples, writes them as `raw/arma3_camera.csv` (pinned by `capture/dotnet/fixtures/arma3_camera.csv`)
  and puts them on QPC as `camera_samples.csv` + `extractor.json` (convention `rfu`: Arma's world is X east, Y north, Z up).
  The game profile `capture/profiles/arma3.json` names the route: `"camera_source": "arma3-mod"`.
- `launcher/OdxArma3/`: `OdxArma3.exe` (net48 WinForms, the addon, `odx_capture_x64.dll` and the capture missions
  embedded). A window: game folder, server (`ip:port`, empty to start into the menu and host or use the editor), password,
  window or fullscreen. On Launch it writes the addon and the extension into `<game>\@OdxCapture\`, packs each
  `mission/capture_<terrain>.<World>` into `Documents\Arma 3\MPMissions\<name>.pbo` (packed, the form a hosting client
  sends to joining players; the unpacked folders earlier versions left there are removed), adds the Windows Firewall rule
  "Odx Arma 3" letting `arma3_x64.exe` in on every network profile (one UAC prompt; Radmin VPN and ZeroTier adapters
  count as public networks, which the firewall's own first-host prompt leaves blocked), remembers the fields in
  `%APPDATA%\OdxArma3\settings.txt` and starts `arma3_x64.exe -mod=@OdxCapture -noSplash -skipIntro -showScriptErrors
  [-window] [-connect=<ip> -port=<port> -password=<pw>]` (the game itself, not BattlEye's wrapper; the addon is unsigned).
  The status line shows this PC's Radmin VPN / ZeroTier IPv4: what the others enter as Server to join a game hosted here
  (Multiplayer → Host Server → a `capture_*` mission). The addon hides the HUD on every client (`showHUD`, again after a
  respawn), so the video is HUD-free whichever difficulty the host picked. It finds the game in
  the last folder used, its own folder or Steam's libraries.

Build: `powershell -File research/arma3-camera-spike/launcher/build.ps1 -OutDir <folder>` on Windows (CMake builds the DLL first, then the net48 exe).
Releases: a tag `arma3-v<version>` (release-arma3.yml) attaches the exe to the release of that name; the window title shows
the version. Players need the server's game version (2.18.152405 on `arma3-server`, document/games/arma3.md).

Checks on a player PC: after a round, the capture's `raw/arma3_camera.csv` exists and `player.json` → `frame_stats.frames_with_camera`
is near the frame count; a warning `no Arma 3 .rpt in …` means the game was not started through the launcher, `no Arma 3
camera rows` that the addon did not load (the `.rpt` then has no `ODXCAM` lines; look for `odx_capture` in its addon list).
**[to verify on the first run]**: the FOV formula against a reprojection, the view codes, and the `.rpt` flush delay (odx-app
waits 3 s for the last rows).
