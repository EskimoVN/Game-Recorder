# Arma 3: video from the game's own Present (plan)

## Why

The video judders even when no frame is lost. Round AM3-023-S01 r19 (2026-11-13): 7244 frames, 7 duplicated, 10 starved,
yet `cadence.py` gives **0 % even steps**: the game rendered at ~48 fps, so on the 30 fps grid the frames behind
consecutive video frames are 21 ms (40 %) or 42 ms (60 %) apart. Motion goes short-long-short. WGC + ffmpeg is not the
problem; the game's frame times are not on our grid.

A reference capture tool for an Unreal title that plays visibly smoother does it inside the game: it hooks
`IDXGISwapChain::Present` (MinHook), converts the backbuffer to NV12 on the game's GPU, hands frames over shared memory
(`ctl_fps`, `ctl_origin_qpc` control fields) and **paces the game's Present to the capture grid** (a limiter in the hook).
Every video frame is then a fresh game frame, one period after the last.

## Compliance

Allowed by the ground rules (`document/games/README.md`): a private server with `BattlEye = 0`, the game started as
`arma3_x64.exe` (not BattlEye's wrapper), our DLL loaded by the engine's own `callExtension`. But `document/games/arma3.md`
§3 says "no hooks": update it before step 2 ships, and check that the licence question (Q13) is unaffected.

## Steps

0. **Measure first, no code.** `python cadence.py <round folder>` on a few rounds now. Then cap the game at 60 fps
   (NVIDIA Control Panel → Manage 3D settings → Program `arma3_x64.exe` → Max Frame Rate 60; AMD: Radeon Chill min = max
   = 60; or RTSS) and record again. A game that holds 60 gives 33 ms steps on the 30 fps grid.
   - Even steps ≥ 99 % on every player's PC: stop here, put the cap in `capture/guides/player-setup.md`.
   - Otherwise (PCs under 60 fps, or present phase drifting against the grid): steps 1-5.
1. **Shared memory contract** (`Local\OdxCapture_<pid>`, cross-language, pinned by a test on each side):
   header `{version, width, height, fps, origin_qpc_ms, capture, slot_count, write_seq}`, then `slot_count` (4) slots
   `{seq, present_qpc_ms, nv12 bytes}`, an auto-reset event `Local\OdxCaptureFrame_<pid>` per written frame. odx record
   owns the control fields, the DLL the slots.
2. **In-game capture in `odx_capture_x64.dll`** (already loaded by the addon via `callExtension`; `capture/native/extractor`
   has `present_hook.cpp` and MinHook). Install the hooks on the first `callExtension`, never in `DllMain` (loader lock).
   Hook `Present` (vtable 8), `Present1` (IDXGISwapChain1, 22) and `ResizeBuffers` (13, drop and recreate textures).
   While `capture` is set, in `Present`: copy the backbuffer (resolve if MSAA), NV12 via `ID3D11VideoProcessor` on the
   game's device (same as `Odx.Recorder/Nv12Converter`), into a ring of 4 staging textures; map the oldest with
   `D3D11_MAP_FLAG_DO_NOT_WAIT` and copy it into the next slot with the QPC of its Present. Never block the render thread
   on the GPU; a busy staging texture is a skipped frame, counted.
3. **Limiter in the same hook.** Before calling the real `Present`, wait for the next tick `origin_qpc_ms + k * period`
   (period = 1000 / fps, or half of it when the PC holds 2× fps: smoother to play, every second frame kept). High
   resolution waitable timer, spin the last 0.5 ms; `SyncInterval` forced to 0 while capturing. Log hook time p50 / p99
   and late presents.
4. **`odx record --source present`**: open the mapping of the game's process, write the control fields, read slots on the
   event into the existing `FrameWriter` (frames are NV12 already: no WGC, no `Nv12Converter`). `t_source_qpc_ms` = the
   slot's Present QPC; grid resampler, `video_frames.csv` (fixture unchanged), audio, finalize unchanged. No mapping
   within 2 s → WGC as today, with the reason in `recording.json` (`"source": "present:arma3_x64"` or the window).
5. **Profile switch**: `capture/profiles/arma3.json` gets `"video_source": "present"`; odx-app passes it to odx record.

## Verify (Windows, 4 players)

- `cadence.py`: even steps ≥ 99 %; duplicates and starved no worse than WGC.
- Players: does play at the capped rate feel acceptable (30 vs 60 option).
- Steam / Discord / RTSS overlays on (they hook Present too; they must not end up in the video or break the chain),
  alt-tab, window resize, fullscreen ↔ window, a respawn, a mission change.
- HDR / 10-bit backbuffer: reject with a clear reason, fall back to WGC.

## Risks

- The limiter caps the player's fps for the whole round; 30 fps play may be rejected by players.
- Readback on the render thread costs frame time; must stay under ~1 ms p99.
- Reforger has no `callExtension` and may run DX12: not covered here.
