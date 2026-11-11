"""Command line: analyse one local video file and write its raw output.

    python -m vision_worker clip.mp4 --out clip.json --model heavy

The plate is looked for among the boxes of the plate proposer, a second model file; --finder shoulders
looks for it the older way, without that file.

With a mark the plate isn't looked for: the marked one is followed from the mark's frame.

    python -m vision_worker clip.mp4 --out clip.json --mark 1.6 250 422 250 220
"""

import argparse
import json
import sys
import time
from pathlib import Path

from vision_worker import models
from vision_worker.bar import Mark, MarkError
from vision_worker.pipeline import analyse
from vision_worker.pose import PoseLandmarkerEstimator, pose_model_name
from vision_worker.proposer import DFineProposer
from vision_worker.raw_output import is_job_id, new_job_id
from vision_worker.video import VideoError, VideoReader

# The lifts a job may name (contracts/openapi/vision-v1.yaml, `Lift`).
LIFTS = ("squat", "bench_press", "deadlift", "overhead_press", "other")


def _job_id(value):
    if not is_job_id(value):
        raise argparse.ArgumentTypeError("must look like job_ followed by a 26-character ULID")
    return value


def _parser():
    parser = argparse.ArgumentParser(
        prog="vision_worker",
        description="Analyse one video file and write per-frame pose and bar detections (raw output v1).",
    )
    parser.add_argument("video", help="path to the video file")
    parser.add_argument("--out", help="where to write the JSON; standard output when omitted")
    parser.add_argument(
        "--model", choices=sorted(models.MODEL_FILES), default="full", help="pose model (default: full)"
    )
    parser.add_argument("--model-dir", help="folder with the model files (default: VISION_MODEL_DIR, then models/)")
    parser.add_argument("--job-id", type=_job_id, help="job id to record (default: a new one)")
    parser.add_argument(
        "--lift",
        choices=LIFTS,
        help="the lift in the video, as the API names it; in a squat the feet are held where they stand "
        "(default: not said, and nothing is held)",
    )
    parser.add_argument(
        "--finder",
        choices=("proposals", "shoulders"),
        help="how the plate is found without a mark: among the boxes a detector model proposes (default), "
        "or by circles that stay put by the shoulders",
    )
    parser.add_argument(
        "--mark",
        nargs=5,
        type=float,
        metavar=("T", "CENTRE_X", "CENTRE_Y", "RIM_X", "RIM_Y"),
        help="follow the plate marked at T seconds: its centre and a point on its rim, in the video's pixels",
    )
    parser.add_argument(
        "--follow-colour",
        action=argparse.BooleanOptionalAction,
        help="with --mark: look for the plate by the colour it has at the mark (default: on)",
    )
    parser.add_argument(
        "--follow-partial-rim",
        action=argparse.BooleanOptionalAction,
        help="with --mark: after the marked frame, accept a part of the rim where the whole isn't found (default: on)",
    )
    return parser


def _finds_by_proposals(args):
    return not args.mark and args.finder != "shoulders"


def main(argv=None):
    parser = _parser()
    args = parser.parse_args(argv)
    if not args.mark and (args.follow_colour is not None or args.follow_partial_rim is not None):
        parser.error("the --follow options need --mark")
    if args.mark and args.finder:
        parser.error("--finder is for finding the plate without a mark: leave it out with --mark")
    mark = None
    if args.mark:
        t, centre_x, centre_y, rim_x, rim_y = args.mark
        mark = Mark(t=t, centre=(centre_x, centre_y), rim=(rim_x, rim_y))
    started = time.perf_counter()
    try:
        model_file = models.model_path(args.model, args.model_dir)
        proposer_file = models.proposer_path(args.model_dir) if _finds_by_proposals(args) else None
        with VideoReader(args.video) as reader:
            estimator = PoseLandmarkerEstimator(model_file)
            proposer = DFineProposer(proposer_file) if proposer_file else None
            try:
                document = analyse(
                    reader,
                    estimator,
                    job_id=args.job_id or new_job_id(),
                    pose_model=pose_model_name(args.model),
                    mark=mark,
                    colour=args.follow_colour is not False,
                    partial_rim=args.follow_partial_rim is not False,
                    proposer=proposer,
                    lift=args.lift,
                )
            finally:
                estimator.close()
                if proposer is not None:
                    proposer.close()
    except (VideoError, models.ModelError, MarkError) as error:
        print(f"error: {error}", file=sys.stderr)
        return 1

    text = json.dumps(document, separators=(",", ":"))
    if args.out:
        out = Path(args.out)
        out.parent.mkdir(parents=True, exist_ok=True)
        out.write_text(text + "\n")
    else:
        sys.stdout.write(text + "\n")

    frames = document["frames"]
    print(
        f"{len(frames)} frames ({document['video']['durationS']:.1f} s of video): "
        f"pose in {sum(f['pose'] is not None for f in frames)}, "
        f"bar in {sum(f['bar'] is not None for f in frames)}, "
        f"took {time.perf_counter() - started:.1f} s",
        file=sys.stderr,
    )
    return 0
