# Arma 3 / Arma Reforger: camera, video and input alignment test

A test to run on a PC with the game, and `analyze.py` to read the result. It answers what the delivered folders alone
cannot: why the camera pose and the picture sometimes disagree by whole frames, whether the third-person view plays a
part, and which frames should get `image_valid = false` in the episode (`frames.parquet`).

## What the delivered folders show

12 delivered player folders (no `raw/`), 2026-10-30 … 2026-11-25, measured with `analyze.py` (30 s windows):

| Game | Build (commit) | Round | cam>video (frames) | Note |
|---|---|---|---|---|
| Reforger | `03c064f` | AMRF-033-S15 | −0.2 | |
| Reforger | `03c064f` | AMRF-039-S09 | **−2.4** | |
| Reforger | `03c064f` | AMRF-050-S06 | **−4.2** | |
| Reforger | `03c064f` | AMRF-091-S05 | **−3.5** | |
| Reforger | `657f49a` | AMRF-350-S13 | −0.02 | |
| Reforger | `657f49a` | AMRF-357-S10 | −0.02 | |
| Arma 3 | `d9e0e22` | AM3-050-S01 | **+2.0** | the only round in third person (whole round) |
| Arma 3 | `af31e77` | AM3-063-S06 | +0.05 | |
| Arma 3 | `af31e77` | AM3-070-S11 | +0.18 | |
| Arma 3 | `657f49a` | AM3-086-S07 | +0.75 | steady, ~25 ms |
| Arma 3 | `657f49a` | AM3-201-S15 | +0.12 | |
| Arma 3 | `c43ae46` | AM3-091-S02 | +0.4 | −2.6 for 10 s at 40 s, camera at ground level |

- The offset is constant through a round (±0.2 frame between windows): a fixed error set when the round starts, not a
  drift. The old Reforger build (`03c064f`, before the Windows clock check of `601c9b7`; those players' clocks were off
  by −548 … +1422 ms) is 80–140 ms off; the current build is not. The two Arma 3 outliers are unexplained.
- mouse>cam (the game's input latency) is ~0.7 frame in Arma 3 and 1.1–3 frames in Reforger. Camera and picture
  still agree, so it is not a sync error.
- No black frames. Frames where the camera turned but the picture did not ("stale"): 5–33 per round on old builds,
  0–7 on current ones. Still pictures of a still scene (0.3–5 %) are real images.

Open questions for this test:

1. Is the third-person view (Arma 3 `EXTERNAL`, Reforger `third_person`) behind the +2 frames of AM3-050-S01?
2. Is the current build within 0.5 frame on every machine, and where does AM3-086-S07's +0.75 come from?
3. Do the recorder's `duplicate` frames (`raw/video_frames.csv`) match the stale frames, so `image_valid` can use them?
4. Map, inventory, menu and optics: what does the episode get while they cover the view?

## Before the test

- odx-app built from this commit or later (episode layout: `episode.json`, `videos/pov.mp4`, `raw/` kept).
- The game started the usual way for capture: Arma 3 through OdxArma3.exe (`@OdxCapture`), Reforger with the
  OdxCapture mod.
- Note for each PC: CPU, GPU, Windows version, the game's FPS cap and average FPS, display mode (fullscreen /
  borderless), the game's resolution and the recorded size, the game's mouse smoothing setting.
- Two players in a room is best (also checks cross-player sync); one player recording alone still answers questions
  1–4.

## The rounds

About 90 s each, every round in both games. Type the task ID as `SYNC-<game>-<n>` (e.g. `SYNC-A3-1`) so the rounds are
easy to find. Write down the clock time of anything unplanned (a crash, an alt-tab, a stutter).

**Round 1, first person.**
1. Stand still, do not touch the mouse for 3 s.
2. Flick the mouse sharply left then right, then wait 2 s. Repeat 6 times.
3. Same with sharp flicks up and down, 4 times.
4. Walk and look around normally for the rest of the round.

**Round 2, third person.** Toggle the view (Num Enter in both games) before the round starts.
1. Steps 1–3 of round 1.
2. From the middle of the round, toggle first/third person every 10 s, with a flick right after each toggle.

**Round 3, overlays.** First person, a flick before and after each step:
1. Map 5 s (M).
2. Inventory 5 s (Arma 3: I, Reforger: Tab).
3. Pause menu 5 s (Esc).
4. Optics: aim down sights 5 s (right mouse button), then binoculars 5 s (Reforger: B; Arma 3: binoculars from the
   inventory, if the loadout has them).
5. Stand still, mouse untouched, 10 s.

## What to send back

- The whole round folder of every round, as odx-app keeps it: `data\captures\<task ID>\`, `raw\` included (zip it; do
  not rename or clean anything).
- odx-app's log of the session and the notes from "Before the test".
- From every player of the room, not only one.

## Reading the result

```bash
python3 research/arma-sync-test/analyze.py <round folder> [<round folder> ...] [--window 10]
```

Needs ffmpeg on PATH, no Python packages. It decodes the video at 64x36 grey (10–30 s per 4 min round) and prints,
per round:

- `cam>video`: median lag between camera rotation (`frames.csv`) and picture change (video), and every window more
  than 1 frame off it (`!` lines, with the Arma 3 view at that time). `+k`: the pose is k frames ahead of the image.
- `mouse>cam`: the game's input latency.
- The view mix: per frame from `raw/arma3_camera.csv`, as row counts from `raw/reforger_camera.csv`; for Arma 3 also
  the median lag per view (question 1).
- still pictures, stale frames, black frames, frames without camera, and the recorder's `duplicate` frames with how
  many of them are stale (question 3).
- `verdict: OK` when the median is within 0.5 frame and no window is more than 1 frame off it; `CHECK` otherwise.

Round 3 answers question 4 by the times of the M / I / Tab / Esc presses in `raw/input_events.csv` (unbound keys are
there too) against the stale and still frames around them.
