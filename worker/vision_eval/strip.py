"""Copies of clips with only the picture: no sound, no location and no other tags (VS3 change 6).

    python -m vision_eval.strip /path/to/clips /path/to/stripped

Run on the machine that holds the clips, before any copy of them leaves it. The video stream is
copied as it is (no re-encoding, so the frames and the rotation are the clip's own); everything else
in the file is left out: sound, data tracks, chapters, and the tags of the file and of the stream,
where phones and editors write the location, the device and the app. Each copy is then read back
with ffprobe and refused if it still has a stream other than the video or a location tag.
"""

import argparse
import json
import subprocess
import sys
from pathlib import Path

CLIP_SUFFIXES = {".mp4", ".mov", ".m4v"}


class StripError(Exception):
    """ffmpeg failed, or a copy still carries what should have gone."""


def _probe(path):
    result = subprocess.run(
        ["ffprobe", "-v", "error", "-show_streams", "-show_format", "-of", "json", str(path)],
        check=False,
        capture_output=True,
        text=True,
    )
    if result.returncode != 0:
        raise StripError(f"ffprobe can't read {path}: {result.stderr.strip()}")
    return json.loads(result.stdout)


def rotation(path):
    """The rotation the video stream asks for, in degrees (0 when none)."""
    stream = next(s for s in _probe(path)["streams"] if s["codec_type"] == "video")
    for side in stream.get("side_data_list", []):
        if "rotation" in side:
            return int(side["rotation"])
    return int(stream.get("tags", {}).get("rotate", 0))


def check_stripped(path):
    info = _probe(path)
    kinds = [s["codec_type"] for s in info["streams"]]
    if kinds != ["video"]:
        raise StripError(f"{path} has streams {kinds}; only one video stream (no audio or data) is allowed")
    tags = dict(info["format"].get("tags", {}))
    tags.update(info["streams"][0].get("tags", {}))
    found = [key for key in tags if "location" in key.lower() or "xyz" in key.lower()]
    if found:
        raise StripError(f"{path} still has location tags: {found}")


def strip_clip(source, target):
    target = Path(target)
    target.parent.mkdir(parents=True, exist_ok=True)
    command = [
        "ffmpeg", "-nostdin", "-v", "error", "-y", "-i", str(source),
        "-map", "0:v:0", "-c", "copy",
        "-map_metadata", "-1", "-map_metadata:s:v", "-1", "-map_chapters", "-1",
        "-fflags", "+bitexact",
        str(target),
    ]  # fmt: skip
    result = subprocess.run(command, check=False, capture_output=True, text=True)
    if result.returncode != 0:
        raise StripError(f"ffmpeg failed on {source}: {result.stderr.strip()}")
    check_stripped(target)
    return target


def strip_folder(source, target):
    """Strips every clip in `source` into `target`, which must be another folder; returns the copies."""
    source, target = Path(source).resolve(), Path(target).resolve()
    if source == target:
        raise StripError("the stripped copies can't go in the same folder as the clips")
    clips = sorted(p for p in source.iterdir() if p.is_file() and p.suffix.lower() in CLIP_SUFFIXES)
    return [strip_clip(clip, target / clip.name) for clip in clips]


def main(argv=None):
    parser = argparse.ArgumentParser(prog="vision_eval.strip", description=__doc__.split("\n\n")[0])
    parser.add_argument("clips", help="folder with the clips")
    parser.add_argument("out", help="folder for the stripped copies (not the clips' own folder)")
    args = parser.parse_args(argv)
    try:
        written = strip_folder(args.clips, args.out)
    except StripError as error:
        print(f"error: {error}", file=sys.stderr)
        return 1
    print(f"{len(written)} clips stripped into {args.out}", file=sys.stderr)
    return 0


if __name__ == "__main__":
    sys.exit(main())
