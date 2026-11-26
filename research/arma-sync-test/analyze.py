#!/usr/bin/env python3
"""Camera/video/input alignment of Arma 3 and Arma Reforger round folders (README.md).

python3 analyze.py <round folder> [<round folder> ...] [--window 10]

Works on both layouts: the episode one (videos/pov.mp4, raw/frames.csv, raw/video_frames.csv, raw/<game>_camera.csv)
and the older delivered player folder (video.mp4, frames.csv at the top, no raw/). Needs ffmpeg on PATH; no packages.

The lags come from correlation, per window:
  cam>video   camera rotation per frame (frames.csv C2W) against how much the picture changes (video, 64x36 grey).
              +k: the picture moves k frames after the pose says it did (pose ahead of the image); 0 is right.
  mouse>cam   mouse dx against yaw change: the game's own input latency, not a sync error.
"""
import argparse
import csv
import json
import math
import os
import statistics
import subprocess
import sys

W, H = 64, 36
STILL = 0.3       # mean absolute grey difference below which two frames count as the same picture
MOVED = 1e-4      # rad of yaw + pitch change per frame above which the camera counts as moving
MIN_R = 0.3       # windows whose correlation peak is weaker than this are left out (nothing moved)


def find(d, name):
    for p in (os.path.join(d, name), os.path.join(d, "raw", name)):
        if os.path.exists(p):
            return p
    return None


def video_of(d):
    for p in (os.path.join(d, "videos", "pov.mp4"), os.path.join(d, "video.mp4")):
        if os.path.exists(p):
            return p
    sys.exit(f"{d}: no videos/pov.mp4 or video.mp4")


def grey_frames(path):
    raw = subprocess.run(["ffmpeg", "-v", "error", "-i", path, "-vf", f"scale={W}:{H},format=gray", "-f", "rawvideo", "-"],
                         check=True, capture_output=True).stdout
    return [raw[i:i + W * H] for i in range(0, len(raw) - W * H + 1, W * H)]


def corr(a, b):
    n = len(a)
    ma, mb = sum(a) / n, sum(b) / n
    va = sum((x - ma) ** 2 for x in a)
    vb = sum((y - mb) ** 2 for y in b)
    if va == 0 or vb == 0:
        return 0.0
    return sum((x - ma) * (y - mb) for x, y in zip(a, b)) / math.sqrt(va * vb)


def peak(a, b, span=6):
    """Lag k (frames, sub-frame by a parabola) maximising corr(a[i], b[i+k]), and the correlation there."""
    c = {}
    for k in range(-span, span + 1):
        c[k] = corr(a[:len(a) - k], b[k:]) if k >= 0 else corr(a[-k:], b[:len(b) + k])
    k = max(c, key=c.get)
    if -span < k < span:
        y0, y1, y2 = c[k - 1], c[k], c[k + 1]
        d = y0 - 2 * y1 + y2
        return k + (0.5 * (y0 - y2) / d if d else 0.0), y1
    return float(k), c[k]


def views(d, frame_qpc):
    """Per frame: the view in the Arma 3 camera log (QPC-stamped, so it lines up with video_frames.csv); else None."""
    path = find(d, "arma3_camera.csv")
    if path is None or frame_qpc is None:
        return None
    rows = [r for r in csv.DictReader(open(path)) if r.get("view")]
    out, j = [], -1
    for t in frame_qpc:
        while j + 1 < len(rows) and float(rows[j + 1]["t_qpc_ms"]) <= t:
            j += 1
        out.append(rows[j]["view"] if j >= 0 else None)
    return out


def reforger_views(d):
    path = find(d, "reforger_camera.csv")
    if path is None:
        return None
    counts = {}
    for line in open(path):
        parts = line.split(",")
        if len(parts) > 3 and parts[0].isdigit():
            key = {"0": "first_person", "1": "third_person", "-1": "no_character"}.get(parts[2], parts[2])
            counts[key] = counts.get(key, 0) + 1
    return counts


def analyse(d, window_s):
    frames_csv = find(d, "frames.csv")
    if frames_csv is None:
        sys.exit(f"{d}: no frames.csv")
    rows = list(csv.DictReader(open(frames_csv)))
    session_path = find(d, "session.json")
    session = json.load(open(session_path)) if session_path else {}
    ts = [int(r["timestamp_ms"]) for r in rows]
    fps = (len(ts) - 1) * 1000 / (ts[-1] - ts[0])

    yaw, pitch, null_cam = [], [], 0
    for r in rows:
        if r["C2W_M00"] == "null":
            null_cam += 1
            yaw.append(yaw[-1] if yaw else 0.0)
            pitch.append(pitch[-1] if pitch else 0.0)
            continue
        # C2W columns are right | up | backward | translation: forward = -column 2, world RFU (x east, y north, z up).
        fx, fy, fz = -float(r["C2W_M02"]), -float(r["C2W_M12"]), -float(r["C2W_M22"])
        yaw.append(math.atan2(fx, fy))
        pitch.append(math.asin(max(-1.0, min(1.0, fz))))
    dyaw = [0.0] + [((yaw[i] - yaw[i - 1] + math.pi) % (2 * math.pi)) - math.pi for i in range(1, len(rows))]
    cam = [0.0] + [abs(dyaw[i]) + abs(pitch[i] - pitch[i - 1]) for i in range(1, len(rows))]
    mouse = [float(r["input_mouse_dx"]) for r in rows]

    grey = grey_frames(video_of(d))
    n = min(len(grey), len(rows))
    diff = [0.0] + [sum(abs(a - b) for a, b in zip(grey[i], grey[i - 1])) / (W * H) for i in range(1, n)]
    black = sum(1 for g in grey[:n] if sum(g) / (W * H) < 8)

    vf_path = find(d, "video_frames.csv")
    vf = list(csv.DictReader(open(vf_path))) if vf_path else None
    duplicate = [r.get("duplicate") == "1" for r in vf][:n] if vf else None
    frame_qpc = [float(r["t_source_qpc_ms"]) for r in vf][:n] if vf else None
    view = views(d, frame_qpc)

    win = max(1, round(window_s * fps))
    windows = []
    for w0 in range(0, n - win + 1, win):
        lag, r = peak(cam[w0:w0 + win], diff[w0:w0 + win])
        mlag, _ = peak(mouse[w0:w0 + win], dyaw[w0:w0 + win])
        v = None
        if view:
            vs = [x for x in view[w0:w0 + win] if x]
            v = max(set(vs), key=vs.count) if vs else None
        windows.append((w0 / fps, lag, r, mlag, v))
    good = [w for w in windows if w[2] >= MIN_R]
    lags = [w[1] for w in good]
    typical = statistics.median(lags) if lags else float("nan")

    still = [i for i in range(1, n) if diff[i] < STILL]
    stale = [i for i in still if cam[i] > MOVED]

    print(f"== {d}")
    print(f"   {session.get('game_title', '?')}  session {session.get('session_id', '?')}  "
          f"tool {session.get('vendor_tool_version', '?')}  {n} frames  {fps:.2f} fps  "
          f"{'episode layout' if os.path.isdir(os.path.join(d, 'raw')) else 'old layout, no raw/'}")
    if lags:
        print(f"   cam>video  median {typical:+.2f} frames ({typical * 1000 / fps:+.0f} ms), "
              f"windows {min(lags):+.2f} .. {max(lags):+.2f} ({len(lags)} of {len(windows)} usable)")
    print(f"   mouse>cam  median {statistics.median(w[3] for w in windows):+.2f} frames (game latency)")
    for t, lag, r, mlag, v in windows:
        if r >= MIN_R and abs(lag - typical) > 1:
            print(f"   ! {t:6.0f} s  cam>video {lag:+.2f} (r {r:.2f})" + (f"  view {v}" if v else ""))
    if view:
        shares = {k: view.count(k) for k in set(view) if k}
        print("   view (arma3_camera.csv): " + ", ".join(f"{k} {100 * c / n:.1f}%" for k, c in sorted(shares.items())))
        for v in sorted(shares):
            vl = [w[1] for w in good if w[4] == v]
            if vl:
                print(f"     {v}: cam>video median {statistics.median(vl):+.2f} over {len(vl)} windows")
    rv = reforger_views(d)
    if rv:
        total = sum(rv.values())
        print("   view (reforger_camera.csv rows): " + ", ".join(f"{k} {100 * c / total:.1f}%" for k, c in sorted(rv.items())))
    line = (f"   still pictures {len(still)} ({100 * len(still) / n:.1f}%), stale (camera moved, picture did not) "
            f"{len(stale)}, black {black}, frames without camera {null_cam}")
    if duplicate is not None:
        dup = [i for i in range(n) if duplicate[i]]
        line += f", recorder duplicates {len(dup)} (of them stale {len(set(dup) & set(stale))})"
    print(line)
    verdict = "OK" if lags and abs(typical) <= 0.5 and all(abs(x - typical) <= 1 for x in lags) else "CHECK"
    print(f"   verdict: {verdict} (median within 0.5 frame and no window more than 1 frame off it)")


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("dirs", nargs="+")
    ap.add_argument("--window", type=float, default=10.0, help="seconds per window (default 10)")
    args = ap.parse_args()
    for d in args.dirs:
        analyse(d.rstrip("/\\"), args.window)


if __name__ == "__main__":
    main()
