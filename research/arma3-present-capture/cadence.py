"""Frame cadence of a recording: how far apart the game frames behind consecutive video frames are.

usage: python cadence.py <round folder or raw/ folder or video_frames.csv> [fps]

Even motion needs every video frame to show a game frame one grid period after the previous one. A game rendering
between 30 and 60 fps on a 30 fps grid alternates 1- and 2-period steps (judder) even with no frame lost.
"""
import csv
import json
import sys
from collections import Counter
from pathlib import Path


def find(arg):
    p = Path(arg)
    if p.is_file():
        return p, p.parent / "recording.json"
    for d in (p, p / "raw"):
        if (d / "video_frames.csv").exists():
            return d / "video_frames.csv", d / "recording.json"
    sys.exit(f"no video_frames.csv in {p}")


EVEN = 0.2  # a step within 20 % of the grid period: 26.7-40 ms at 30 fps; a 48 fps game gives 21 / 42 ms steps


def steps(source_ms):
    return [b - a for a, b in zip(source_ms, source_ms[1:])]


def even_share(step_ms, fps):
    period = 1000 / fps
    return sum(abs(s / period - 1) <= EVEN for s in step_ms) / len(step_ms)


def main():
    frames_csv, recording = find(sys.argv[1])
    fps = float(sys.argv[2]) if len(sys.argv) > 2 else json.loads(recording.read_text())["fps"]
    with frames_csv.open(newline="") as f:
        source = [float(r["t_source_qpc_ms"]) for r in csv.DictReader(f)]
    step_ms = steps(source)
    counts = Counter(round(s) for s in step_ms)
    for ms, n in counts.most_common(8):
        print(f"{ms:5d} ms: {n:6d}  {100 * n / len(step_ms):5.1f} %")
    print(f"even steps: {100 * even_share(step_ms, fps):.1f} % (goal >= 99 %)")


assert even_share(steps([0, 33.3, 66.7, 100, 121, 163]), 30) == 0.6

if __name__ == "__main__":
    main()
