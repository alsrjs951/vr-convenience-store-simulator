"""Looks at and cuts what FilmRecorder filmed (-p2-film): one .mkv per camera, all in step, marks.csv, and the game's
own sound in sound.wav (in step too; a take without it gives a silent film).

  python film_cut.py sheet TAKE [--at SECONDS ...] [--marks PATTERN]
      One picture per moment with every camera side by side, into TAKE/sheets. Without --at or --marks, every
      10 seconds. For choosing which camera to show when.
  python film_cut.py cut TAKE EDL.json OUT.mp4 [--bed MUSIC --bed-gain DB]
      Cuts by an edit list. Each entry: {"cam": name, "at": mark pattern or seconds, "offset": s, "dur": s,
      "caption": text, "speed": 1.0}; or {"file": video, "start": s, "dur": s, "caption": text} for footage from
      elsewhere (a phone video); or {"slate": text, "dur": s} for a title card. "mute": true leaves a piece silent.
      "take": folder takes a camera piece (its camera, marks and sound) from another take, named beside TAKE.
      "step": text puts a label in the top left corner (which part of the game this is).
      "zoom": [times, x, y] enlarges the picture around a point (0..1 across and down). "inset": true makes the
      picture smaller and stands it above a dark band, so the caption covers none of it (for a shot of a screen).
      --bed lays a piece of music under the whole film, looped, at --bed-gain dB (default -22), faded in and out.

The marks are the film's own time: a mark pattern is a regular expression matched against the mark text, first match.
"""
import argparse
import csv
import json
import math
import re
import subprocess
import sys
from pathlib import Path

FONT = Path(__file__).resolve().parents[2] / "Assets" / "Resources" / "Fonts" / "Pretendard-Bold.ttf"
WIDTH, HEIGHT, FPS = 1920, 1080, 30


def read_marks(take):
    with open(take / "marks.csv", encoding="utf-8") as handle:
        return [(float(row["seconds"]), row["mark"]) for row in csv.DictReader(handle)]


def mark_time(marks, pattern):
    if isinstance(pattern, (int, float)):
        return float(pattern)
    for seconds, mark in marks:
        if re.search(pattern, mark):
            return seconds
    sys.exit(f"no mark matches {pattern!r}")


def cameras(take):
    return sorted(path for path in take.glob("*.mkv"))


def run(arguments):
    done = subprocess.run(arguments, capture_output=True, text=True, encoding="utf-8", errors="replace")
    if done.returncode != 0:
        sys.exit(f"ffmpeg failed:\n{' '.join(str(a) for a in arguments)}\n{done.stderr[-3000:]}")


def font_for_filter():
    # Inside a filter a Windows path needs its colon escaped and forward slashes.
    return str(FONT).replace("\\", "/").replace(":", "\\:")


def escape_text(text):
    return text.replace("\\", "\\\\").replace(":", "\\:").replace("'", "’").replace("%", "\\%").replace(",", "\\,")


def sheet(take, moments, labels):
    files = cameras(take)
    out = take / "sheets"
    out.mkdir(exist_ok=True)
    columns = 4 if len(files) > 4 else max(1, len(files))
    rows = math.ceil(len(files) / columns)
    tile_w, tile_h = 640, 360
    for seconds, label in zip(moments, labels):
        arguments = ["ffmpeg", "-y", "-loglevel", "error"]
        for file in files:
            arguments += ["-ss", f"{seconds:.3f}", "-i", str(file)]
        parts = []
        for index, file in enumerate(files):
            parts.append(f"[{index}:v]scale={tile_w}:{tile_h},drawtext=fontfile='{font_for_filter()}':text='{escape_text(file.stem)}':x=10:y=10:fontsize=24:fontcolor=white:box=1:boxcolor=black@0.6:boxborderw=6[t{index}]")
        layout = "|".join(f"{(index % columns) * tile_w}_{(index // columns) * tile_h}" for index in range(len(files)))
        parts.append("".join(f"[t{index}]" for index in range(len(files))) + f"xstack=inputs={len(files)}:layout={layout}:fill=black[out]")
        name = out / f"{seconds:07.2f}_{re.sub(r'[^A-Za-z0-9]+', '_', label)[:50]}.jpg"
        arguments += ["-filter_complex", ";".join(parts), "-map", "[out]", "-frames:v", "1", "-q:v", "3", str(name)]
        run(arguments)
        print(name)


def cut(take, edl_path, out_path, bed=None, bed_gain=-22.0):
    main_take, marks_of = take, {}
    edl = json.loads(Path(edl_path).read_text(encoding="utf-8"))
    arguments = ["ffmpeg", "-y", "-loglevel", "error"]
    parts, total, inputs = [], 0.0, 0
    for index, entry in enumerate(edl):
        # A piece is from the take the film is cut from, unless it names another one.
        take = main_take.parent / entry["take"] if entry.get("take") else main_take
        if "cam" in entry and take not in marks_of:
            marks_of[take] = read_marks(take)
        marks, sound = marks_of.get(take), take / "sound.wav"
        duration = float(entry["dur"])
        speed = float(entry.get("speed", 1.0))
        heard = None
        if "slate" in entry:
            arguments += ["-f", "lavfi", "-t", f"{duration:.3f}", "-i", f"color=c=0x101418:s={WIDTH}x{HEIGHT}:r={FPS}"]
            chain = f"[{inputs}:v]format=yuv420p"
            inputs += 1
            lines = entry["slate"].split("\n")
            for row, line in enumerate(lines):
                size = 84 if row == 0 else 44
                y = f"(h/2)-{(len(lines) - 1) * 40}+{row * 110}-{size // 2}"
                chain += f",drawtext=fontfile='{font_for_filter()}':text='{escape_text(line)}':x=(w-text_w)/2:y={y}:fontsize={size}:fontcolor=white"
        else:
            if "file" in entry:
                source, start = entry["file"], float(entry.get("start", 0.0))
            else:
                source = str(take / (entry["cam"] + ".mkv"))
                start = mark_time(marks, entry.get("at", 0.0)) + float(entry.get("offset", 0.0))
            start = max(0.0, start)
            arguments += ["-ss", f"{start:.3f}", "-t", f"{duration * speed:.3f}", "-i", source]
            chain = (f"[{inputs}:v]setpts=(PTS-STARTPTS)/{speed},fps={FPS},scale={WIDTH}:{HEIGHT}:force_original_aspect_ratio=increase,"
                     f"crop={WIDTH}:{HEIGHT},setsar=1,format=yuv420p")
            inputs += 1
            if entry.get("zoom"):
                times, x, y = entry["zoom"]
                wide, tall = int(WIDTH * times / 2) * 2, int(HEIGHT * times / 2) * 2
                left = int(min(max(x * wide - WIDTH / 2, 0), wide - WIDTH))
                top = int(min(max(y * tall - HEIGHT / 2, 0), tall - HEIGHT))
                chain += f",scale={wide}:{tall},crop={WIDTH}:{HEIGHT}:{left}:{top},setsar=1"
            if entry.get("inset"):
                chain += f",scale=1612:906,pad={WIDTH}:{HEIGHT}:(ow-iw)/2:20:color=0x101418,setsar=1"
            # The take's own sound for what the take's cameras show; footage from elsewhere stays silent.
            if "cam" in entry and sound.exists() and not entry.get("mute"):
                arguments += ["-ss", f"{start:.3f}", "-t", f"{duration * speed:.3f}", "-i", str(sound)]
                heard = f"[{inputs}:a]atempo={speed},"
                inputs += 1
            print(f"{total:6.1f}s  {duration:4.1f}s  {entry.get('cam', Path(source).name):12s} from {start:7.2f}s  {entry.get('caption', '')}")
        if entry.get("caption"):
            chain += (f",drawtext=fontfile='{font_for_filter()}':text='{escape_text(entry['caption'])}':x=(w-text_w)/2:y=h-150:fontsize=58:"
                      f"fontcolor=white:box=1:boxcolor=black@0.55:boxborderw=22")
        if entry.get("step"):
            chain += (f",drawtext=fontfile='{font_for_filter()}':text='{escape_text(entry['step'])}':x=64:y=56:fontsize=52:"
                      f"fontcolor=white:box=1:boxcolor=0x041E42@0.9:boxborderw=20")
        parts.append(chain + f"[v{index}]")
        if heard is None:
            arguments += ["-f", "lavfi", "-t", f"{duration:.3f}", "-i", "anullsrc=r=48000:cl=stereo"]
            heard = f"[{inputs}:a]"
            inputs += 1
        # Every piece exactly as long as its picture, with a short fade at each end so a cut does not click.
        fade = min(0.08, duration / 4)
        parts.append(heard + f"aresample=48000,aformat=sample_fmts=fltp:channel_layouts=stereo,apad,atrim=0:{duration:.3f},"
                             f"afade=t=in:d={fade:.3f},afade=t=out:st={duration - fade:.3f}:d={fade:.3f},asetpts=PTS-STARTPTS[a{index}]")
        total += duration
    parts.append("".join(f"[v{index}][a{index}]" for index in range(len(edl))) + f"concat=n={len(edl)}:v=1:a=1[out][{'said' if bed else 'sound'}]")
    if bed:
        arguments += ["-stream_loop", "-1", "-i", str(bed)]
        parts.append(f"[{inputs}:a]aresample=48000,aformat=sample_fmts=fltp:channel_layouts=stereo,volume={bed_gain}dB,atrim=0:{total:.3f},"
                     f"afade=t=in:d=1.0,afade=t=out:st={total - 2.5:.3f}:d=2.5[bed];[said][bed]amix=inputs=2:normalize=0:duration=first[sound]")
    # The filter graph goes in a file: with some forty pieces it is longer than a Windows command line may be.
    graph = Path(out_path).with_suffix(".filter.txt")
    graph.write_text(";\n".join(parts), encoding="utf-8")
    arguments += ["-/filter_complex", str(graph), "-map", "[out]", "-map", "[sound]", "-c:v", "libx264", "-preset", "medium", "-crf", "18", "-pix_fmt", "yuv420p",
                  "-c:a", "aac", "-b:a", "192k", "-movflags", "+faststart", str(out_path)]
    run(arguments)
    graph.unlink()
    print(f"{out_path}: {total:.1f} s, {len(edl)} pieces")


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    commands = parser.add_subparsers(dest="command", required=True)
    sheets = commands.add_parser("sheet")
    sheets.add_argument("take", type=Path)
    sheets.add_argument("--at", type=float, nargs="*", default=[])
    sheets.add_argument("--marks", default=None)
    cuts = commands.add_parser("cut")
    cuts.add_argument("take", type=Path)
    cuts.add_argument("edl")
    cuts.add_argument("out")
    cuts.add_argument("--bed", type=Path, default=None)
    cuts.add_argument("--bed-gain", type=float, default=-22.0)
    options = parser.parse_args()
    if options.command == "sheet":
        marks = read_marks(options.take)
        moments = list(options.at)
        labels = [f"at_{seconds:.1f}" for seconds in moments]
        if options.marks:
            for seconds, mark in marks:
                if re.search(options.marks, mark):
                    moments.append(seconds)
                    labels.append(mark)
        if not moments:
            end = marks[-1][0]
            moments = [float(s) for s in range(0, int(end), 10)]
            labels = [f"at_{seconds:.0f}" for seconds in moments]
        sheet(options.take, moments, labels)
    else:
        cut(options.take, options.edl, options.out, options.bed, options.bed_gain)


if __name__ == "__main__":
    main()
