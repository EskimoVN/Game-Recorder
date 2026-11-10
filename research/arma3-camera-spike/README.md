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
- `launcher/OdxArma3/`: `OdxArma3.exe` (net48 WinForms, ~40 KB with the addon embedded). A window: game folder, server
  (`ip` or `ip:port`, default port 2302), password (default `odx`, see `capture/deploy/aws/arma3/server.cfg`), window or
  fullscreen, profile folder. On Launch it packs the embedded addon into `<game>\@OdxCapture\addons\odx_capture.pbo`
  (the folder replaced as a whole; `Pbo` mirrors `capture/deploy/aws/arma3/pack_pbo.py`), remembers the fields in
  `%APPDATA%\OdxArma3\settings.txt` (odx-app reads `profile=` there to find the `.rpt`) and starts
  `arma3_x64.exe -noLauncher -noSplash -skipIntro -mod=@OdxCapture -profiles=<profile> -name=odx -connect=<ip> -port=<port> -password=<pw>`
  (the game itself, not BattlEye's wrapper: the Odx servers run `BattlEye = 0` and `verifySignatures = 0`, the addon
  being unsigned). A profile folder inside OneDrive is refused: syncing locks the game's files. It finds the game in the
  last folder used, its own folder or Steam's libraries, and puts an "Odx Arma 3" shortcut on the Desktop on the first run.

Build: `dotnet build -c Release research/arma3-camera-spike/launcher/OdxArma3` → `bin/Release/net48/OdxArma3.exe`.
Releases: a tag `arma3-v<version>` (release-arma3.yml) attaches the exe to the release of that name; the window title shows
the version. Players need the server's game version (2.18.152405 on `arma3-server`, document/games/arma3.md).

Checks on a player PC: after a round, the capture's `raw/arma3_camera.csv` exists and `player.json` → `frame_stats.frames_with_camera`
is near the frame count; a warning `no Arma 3 .rpt in …` means the game was not started through the launcher, `no Arma 3
camera rows` that the addon did not load (the `.rpt` then has no `ODXCAM` lines; look for `odx_capture` in its addon list).
**[to verify on the first run]**: the FOV formula against a reprojection, the view codes, and the `.rpt` flush delay (odx-app
waits 3 s for the last rows).
