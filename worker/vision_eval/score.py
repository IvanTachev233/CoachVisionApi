"""Bar scores: how often the worker reports a bar, and how often it is the plate a person labelled.

Reads the worker's raw outputs (contracts/internal/raw-output/v1.json), stored as
<model>/<clip>.json, and the plate labels (`vision_eval.labels`), and prints one table per
model with a row per clip and a totals row. It works on numbers only: no clip is opened.

    python -m vision_eval.score /path/to/raw-output /path/to/clips/plate-labels.csv
"""

import argparse
import json
import math
import sys
from dataclasses import dataclass, fields
from pathlib import Path

from vision_eval.labels import LabelError, read_labels

# The raw-output version this module reads. Its own figure, not the worker's: a new version needs new reading code.
SCHEMA_VERSION = "1.0"

# How far a bar may be from the label and still be right, both as a share of the labelled radius.
CENTRE_TOLERANCE = 0.25
RADIUS_TOLERANCE = 0.10

# A label and its frame carry the same time, each rounded when it was written.
TIME_TOLERANCE_S = 0.001

HEADINGS = ("Clip", "Frames with a bar", "Labelled frames with a bar", "Centre right", "Radius right", "Both right")
MADE_BY = (("Bar detector", "barDetector"), ("Pose model", "poseModel"), ("Worker", "version"))


class ScoreError(Exception):
    """A raw output can't be read, or it and its labels don't belong together."""


@dataclass(frozen=True)
class Score:
    """Counts for one raw output, or for several added up."""

    frames: int = 0
    with_bar: int = 0  # frames where a bar was reported
    labelled: int = 0  # labelled frames where the plate can be seen
    found: int = 0  # of those: a bar was reported
    centre_right: int = 0  # of those: the bar's centre is close enough to the labelled one
    radius_right: int = 0  # of those: the bar's radius is close enough to the labelled one
    right: int = 0  # of those: both
    hidden: int = 0  # labelled frames where the plate is hidden; not in the counts above
    hidden_with_bar: int = 0  # of those: a bar was reported all the same


@dataclass(frozen=True)
class ModelScores:
    """The scores of one folder of outputs, <model>/<clip>.json."""

    model: str
    clips: dict  # clip name -> Score, in name order
    made_by: dict  # "barDetector", "poseModel", "version" -> the values the outputs carry
    missing: list  # labelled clips with no output in the folder


def score_output(document, labels, *, centre_tolerance=CENTRE_TOLERANCE, radius_tolerance=RADIUS_TOLERANCE):
    """Score one raw output against the labels of its clip.

    A label belongs to the frame with its number. Both tolerances are shares of the labelled
    radius, so the same figures hold for a near plate and a far one.
    """
    video = document["video"]
    frames = {frame["index"]: frame for frame in document["frames"]}
    counts = dict.fromkeys((field.name for field in fields(Score)), 0)
    counts["frames"] = len(document["frames"])
    counts["with_bar"] = sum(frame["bar"] is not None for frame in document["frames"])

    for label in labels:
        if (label.video_width, label.video_height) != (video["width"], video["height"]):
            raise ScoreError(
                f"the label on frame {label.frame} is for a {label.video_width}x{label.video_height} video, "
                f"the output is {video['width']}x{video['height']}"
            )
        frame = frames.get(label.frame)
        if frame is None:
            raise ScoreError(f"the output has no frame {label.frame}, which has a label")
        if abs(frame["t"] - label.t) > TIME_TOLERANCE_S:
            raise ScoreError(f"frame {label.frame} is at {frame['t']} s in the output and at {label.t} s in its label")

        bar = frame["bar"]
        if not label.visible:
            counts["hidden"] += 1
            counts["hidden_with_bar"] += bar is not None
            continue
        counts["labelled"] += 1
        if bar is None:
            continue
        radius = label.radius_px
        centre_right = math.dist((bar["x"], bar["y"]), label.centre) <= centre_tolerance * radius
        radius_right = abs(bar["radiusPx"] - radius) <= radius_tolerance * radius
        counts["found"] += 1
        counts["centre_right"] += centre_right
        counts["radius_right"] += radius_right
        counts["right"] += centre_right and radius_right
    return Score(**counts)


def total(scores):
    """The counts of `scores` added up."""
    scores = list(scores)
    return Score(**{field.name: sum(getattr(score, field.name) for score in scores) for field in fields(Score)})


def read_output(path):
    """The raw output in `path`."""
    try:
        document = json.loads(Path(path).read_text())
    except (OSError, ValueError) as error:
        raise ScoreError(f"can't be read as JSON: {error}") from error
    if not isinstance(document, dict) or document.get("schemaVersion") != SCHEMA_VERSION:
        raise ScoreError(f"not a raw output of version {SCHEMA_VERSION}")
    return document


def score_folder(outputs, labels, *, centre_tolerance=CENTRE_TOLERANCE, radius_tolerance=RADIUS_TOLERANCE):
    """Score every <model>/<clip>.json under `outputs` against the labels of the clip with that name.

    One `ModelScores` per folder that holds outputs, in name order.
    """
    by_clip = {}
    for label in labels:
        by_clip.setdefault(Path(label.file).stem, []).append(label)

    outputs = Path(outputs)
    folders = sorted(path for path in outputs.iterdir() if path.is_dir()) if outputs.is_dir() else []
    models = []
    for folder in folders:
        clips = {}
        made_by = {key: set() for _, key in MADE_BY}
        for file in sorted(folder.glob("*.json")):
            try:
                document = read_output(file)
                clips[file.stem] = score_output(
                    document,
                    by_clip.get(file.stem, []),
                    centre_tolerance=centre_tolerance,
                    radius_tolerance=radius_tolerance,
                )
            except ScoreError as error:
                raise ScoreError(f"{file}: {error}") from error
            for key, values in made_by.items():
                values.add(document["worker"][key])
        if clips:
            models.append(ModelScores(folder.name, clips, made_by, sorted(set(by_clip) - set(clips))))
    return models


def _share(part, whole):
    return "-" if whole == 0 else f"{part}/{whole} ({100 * part / whole:.0f}%)"


def _row(name, score):
    return [
        name,
        _share(score.with_bar, score.frames),
        _share(score.found, score.labelled),
        _share(score.centre_right, score.labelled),
        _share(score.radius_right, score.labelled),
        _share(score.right, score.labelled),
    ]


def _table(rows):
    """A Markdown table that also reads as plain text: names to the left, figures to the right."""
    rows = [list(HEADINGS), *rows]
    widths = [max(len(row[column]) for row in rows) for column in range(len(HEADINGS))]
    rule = ["-" * widths[0], *("-" * (width - 1) + ":" for width in widths[1:])]
    lines = []
    for row in (rows[0], rule, *rows[1:]):
        cells = [row[0].ljust(widths[0]), *(cell.rjust(width) for cell, width in zip(row[1:], widths[1:], strict=True))]
        lines.append("| " + " | ".join(cells) + " |")
    return "\n".join(lines)


def render(models, *, outputs, labels, centre_tolerance=CENTRE_TOLERANCE, radius_tolerance=RADIUS_TOLERANCE, notes=()):
    """The tables as Markdown text: what was scored, what counts as right, any `notes`, then a part per model."""
    parts = [
        "# Bar scores",
        f"- Raw outputs: `{outputs}`\n- Plate labels: `{labels}`",
        (
            f"A bar is right when its centre is within {centre_tolerance:g} of the labelled radius of the labelled "
            f"centre and its radius is within {100 * radius_tolerance:g}% of the labelled radius. A labelled frame "
            "without a bar is not right. Labelled frames where the plate is hidden are left out of the columns and "
            "counted under each table."
        ),
        *notes,
    ]
    for scores in models:
        whole = total(scores.clips.values())
        count = len(scores.clips)
        rows = [_row(clip, score) for clip, score in scores.clips.items()]
        rows.append(_row(f"Total, {count} clip{'' if count == 1 else 's'}", whole))
        notes = [f"Labelled frames with the plate hidden: {whole.hidden}. A bar was reported on: {whole.hidden_with_bar}."]
        if scores.missing:
            notes.append(f"No output for labelled clips: {', '.join(scores.missing)}.")
        parts += [
            f"## {scores.model}",
            " ".join(f"{name}: {', '.join(sorted(scores.made_by[key]))}." for name, key in MADE_BY),
            _table(rows),
            "\n".join(notes),
        ]
    return "\n\n".join(parts) + "\n"


def _parser():
    parser = argparse.ArgumentParser(
        prog="vision_eval.score",
        description="Score the bar in stored raw outputs against the plate labels: a table per model on standard output.",
    )
    parser.add_argument("outputs", help="folder with the raw outputs, as <model>/<clip>.json")
    parser.add_argument("labels", help="the plate label file (plate-labels.csv)")
    parser.add_argument("--save", metavar="FILE", help="also write the tables to this file")
    parser.add_argument(
        "--centre-tolerance",
        type=float,
        default=CENTRE_TOLERANCE,
        help=f"how far the centre may be off, as a share of the labelled radius (default: {CENTRE_TOLERANCE:g})",
    )
    parser.add_argument(
        "--radius-tolerance",
        type=float,
        default=RADIUS_TOLERANCE,
        help=f"how far the radius may be off, as a share of the labelled radius (default: {RADIUS_TOLERANCE:g})",
    )
    return parser


def main(argv=None):
    args = _parser().parse_args(argv)
    tolerances = {"centre_tolerance": args.centre_tolerance, "radius_tolerance": args.radius_tolerance}
    try:
        labels = read_labels(args.labels)
        if not labels:
            raise ScoreError(f"no labels in {args.labels}")
        models = score_folder(args.outputs, labels, **tolerances)
        if not models:
            raise ScoreError(f"no raw outputs in {args.outputs}: expected <model>/<clip>.json")
    except (LabelError, ScoreError) as error:
        print(f"error: {error}", file=sys.stderr)
        return 1

    text = render(models, outputs=args.outputs, labels=args.labels, **tolerances)
    sys.stdout.write(text)
    if args.save:
        saved = Path(args.save)
        saved.parent.mkdir(parents=True, exist_ok=True)
        saved.write_text(text)
    return 0


if __name__ == "__main__":
    sys.exit(main())
