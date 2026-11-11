"""Plate labels: where a person says the plate is, in a few frames of each clip.

A label is two clicks on one frame, the plate's centre and a point on its rim, both in the
video's pixels (the upright frame, as the worker's raw output uses them). A frame where the
plate can't be seen has a label without clicks. The file holds numbers only.
"""

import csv
import math
import os
from dataclasses import dataclass
from pathlib import Path

COLUMNS = (
    "file",
    "frame",
    "t",
    "visible",
    "centre_x",
    "centre_y",
    "rim_x",
    "rim_y",
    "radius_px",
    "video_width",
    "video_height",
)


class LabelError(Exception):
    """A label, or the label file, is not in the expected form."""


@dataclass(frozen=True)
class PlateLabel:
    file: str  # the clip's file name
    frame: int  # the frame's number in the video, from 0, as in the raw output
    t: float  # seconds from the start of the video
    video_width: int
    video_height: int
    centre: tuple[float, float] | None = None  # None with `rim` None: the plate is hidden in this frame
    rim: tuple[float, float] | None = None

    def __post_init__(self):
        if (self.centre is None) != (self.rim is None):
            raise LabelError("a label needs both the centre and the rim click, or neither")
        if self.centre is not None and math.dist(self.centre, self.rim) == 0:
            raise LabelError("the centre and the rim click are the same point")

    @property
    def visible(self):
        return self.centre is not None

    @property
    def radius_px(self):
        """The plate's radius: how far the rim click is from the centre click."""
        return math.dist(self.centre, self.rim) if self.visible else None


def pick_frames(frame_count, wanted=5):
    """Frame numbers spread evenly over a clip: the middles of `wanted` equal stretches."""
    if frame_count <= wanted:
        return list(range(frame_count))
    return [int((i + 0.5) * frame_count / wanted) for i in range(wanted)]


def replace_clip(labels, file, new):
    """`labels` with the rows of clip `file` swapped for `new`, ordered by clip and frame."""
    kept = [label for label in labels if label.file != file]
    return sorted([*kept, *new], key=lambda label: (label.file, label.frame))


def labelled_files(labels):
    """The clips that have labels. A clip's rows are written together, so any row means it was finished."""
    return {label.file for label in labels}


def _row(label):
    clicks = ["", "", "", "", ""]
    if label.visible:
        clicks = [f"{v:.1f}" for v in (*label.centre, *label.rim, label.radius_px)]
    return [
        label.file,
        str(label.frame),
        f"{label.t:.4f}",
        "yes" if label.visible else "no",
        *clicks,
        str(label.video_width),
        str(label.video_height),
    ]


def _label(row):
    seen = {"yes": True, "no": False}[row["visible"]]
    return PlateLabel(
        file=row["file"],
        frame=int(row["frame"]),
        t=float(row["t"]),
        video_width=int(row["video_width"]),
        video_height=int(row["video_height"]),
        centre=(float(row["centre_x"]), float(row["centre_y"])) if seen else None,
        rim=(float(row["rim_x"]), float(row["rim_y"])) if seen else None,
    )


def read_labels(path):
    """The labels in `path`; none if the file doesn't exist yet."""
    path = Path(path)
    if not path.is_file():
        return []
    with path.open(newline="") as handle:
        reader = csv.DictReader(handle)
        if tuple(reader.fieldnames or ()) != COLUMNS:
            raise LabelError(f"{path} is not a plate label file: its columns are {reader.fieldnames}")
        labels = []
        for row in reader:
            try:
                labels.append(_label(row))
            except (KeyError, TypeError, ValueError, LabelError) as error:
                raise LabelError(f"{path}, line {reader.line_num}: {error!r}") from error
    return labels


def write_labels(path, labels):
    """Write `labels` to `path`, ordered by clip and frame. The old file stays whole until the new one is ready."""
    path = Path(path)
    path.parent.mkdir(parents=True, exist_ok=True)
    partial = path.with_name(f".{path.name}.partial")
    with partial.open("w", newline="") as handle:
        writer = csv.writer(handle, lineterminator="\n")
        writer.writerow(COLUMNS)
        for label in sorted(labels, key=lambda label: (label.file, label.frame)):
            writer.writerow(_row(label))
    os.replace(partial, path)
