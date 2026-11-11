"""Tries each plate label as the mark: follows the plate from it and scores the result on the clip's other labels.

For every labelled clip the frames are read once, without the pose model, since following a mark
doesn't use the pose. Each label where the plate can be seen is then the mark of one run; the run's
raw output is kept as marked/<clip>.mark-<frame>.json, and scored against the clip's other labels
(`vision_eval.score`). A clip's row adds up its runs. The table is printed and saved as
bar-scores.md in the output folder. Only numbers are written: no frame is shown or stored.

    python -m vision_eval.marked /path/to/clips /path/to/marked-outputs
"""

import argparse
import json
import sys
from pathlib import Path

from vision_eval.labels import LabelError, read_labels
from vision_eval.score import (
    CENTRE_TOLERANCE,
    RADIUS_TOLERANCE,
    ModelScores,
    render,
    score_output,
    total,
)
from vision_worker import __version__
from vision_worker.bar import MARKED_DETECTOR, BarTracker, marked_detector
from vision_worker.raw_output import build_document, new_job_id
from vision_worker.video import VideoError, VideoReader

POSE_MODEL = "not-run"


def follow_marks(clip, labels, *, colour=True, partial_rim=True):
    """One raw output per visible label of `clip`, with that label as the mark: [(label, document)].

    `colour` and `partial_rim` are the follower's options (`BarTracker.follow`), on unless turned off.
    """
    with VideoReader(clip) as reader:
        tracker = None
        rows = []
        for frame in reader:
            if tracker is None:
                height, width = frame.image.shape[:2]
                tracker = BarTracker(width, height, colour=colour)
            tracker.add(frame.image, None)
            rows.append((frame.index, frame.t))
        video = reader.summary()
    place = {index: position for position, (index, _) in enumerate(rows)}

    runs = []
    for label in labels:
        if not label.visible:
            continue
        if label.frame not in place:
            raise LabelError(f"{clip.name} has no frame {label.frame}, which has a label")
        bars = tracker.follow(place[label.frame], label.centre, label.rim, colour=colour, partial_rim=partial_rim)
        document = build_document(
            job_id=new_job_id(),
            pose_model=POSE_MODEL,
            video=video,
            frames=[(index, t, bar, None) for (index, t), bar in zip(rows, bars, strict=True)],
            bar_detector=marked_detector(colour, partial_rim),
        )
        runs.append((label, document))
    return runs


def _parser():
    parser = argparse.ArgumentParser(
        prog="vision_eval.marked",
        description="Follow the plate from each plate label in turn and score every run on the clip's other labels.",
    )
    parser.add_argument("clips", help="folder with the clips and their plate-labels.csv")
    parser.add_argument("out", help="folder for the runs' raw outputs and the table (bar-scores.md)")
    parser.add_argument(
        "--colour",
        action=argparse.BooleanOptionalAction,
        default=True,
        help="follow by the colour the plate has at the mark (default: on)",
    )
    parser.add_argument(
        "--partial-rim",
        action=argparse.BooleanOptionalAction,
        default=True,
        help="after the mark, accept a part of the rim where the whole isn't found (default: on)",
    )
    parser.add_argument("--labels", help="the plate label file (default: plate-labels.csv in the clips folder)")
    parser.add_argument("--centre-tolerance", type=float, default=CENTRE_TOLERANCE, help="as in vision_eval.score")
    parser.add_argument("--radius-tolerance", type=float, default=RADIUS_TOLERANCE, help="as in vision_eval.score")
    return parser


def main(argv=None):
    args = _parser().parse_args(argv)
    folder, out = Path(args.clips), Path(args.out)
    label_file = Path(args.labels) if args.labels else folder / "plate-labels.csv"
    options = {"colour": args.colour, "partial_rim": args.partial_rim}
    detector = marked_detector(**options)
    # The runs' folder and the table's heading say which options were on: marked, marked+colour, ...
    runs = "marked" + detector.removeprefix(MARKED_DETECTOR)
    tolerances = {"centre_tolerance": args.centre_tolerance, "radius_tolerance": args.radius_tolerance}
    try:
        labels = read_labels(label_file)
        if not labels:
            raise LabelError(f"no labels in {label_file}")
        by_clip = {}
        for label in labels:
            by_clip.setdefault(label.file, []).append(label)

        clips = {}
        tried = unmeasured = 0
        for file, clip_labels in sorted(by_clip.items()):
            scores = []
            for mark, document in follow_marks(folder / file, clip_labels, **options):
                tried += 1
                unmeasured += all(frame["bar"] is None for frame in document["frames"])
                kept = out / runs / f"{Path(file).stem}.mark-{mark.frame:05d}.json"
                kept.parent.mkdir(parents=True, exist_ok=True)
                kept.write_text(json.dumps(document, separators=(",", ":")) + "\n")
                others = [label for label in clip_labels if label is not mark]
                scores.append(score_output(document, others, **tolerances))
            clips[Path(file).stem] = total(scores)
            print(f"{file}: {len(scores)} marks followed", file=sys.stderr)
    except (LabelError, VideoError) as error:
        print(f"error: {error}", file=sys.stderr)
        return 1

    made_by = {"barDetector": {detector}, "poseModel": {POSE_MODEL}, "version": {__version__}}
    text = render(
        [ModelScores(runs, clips, made_by, [])],
        outputs=out / runs,
        labels=label_file,
        notes=[
            (
                "Each label where the plate can be seen was the mark of one run, which is scored on the clip's "
                "other labels, never on its own. A clip's row adds up its runs."
            ),
            f"Marks tried: {tried}. No plate measured at the mark: {unmeasured}.",
        ],
        **tolerances,
    )
    sys.stdout.write(text)
    (out / "bar-scores.md").write_text(text)
    return 0


if __name__ == "__main__":
    sys.exit(main())
