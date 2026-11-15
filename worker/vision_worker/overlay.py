"""Draws a raw output over its video, so a person can see what the worker found.

    python -m vision_worker.overlay clip.mp4 clip.json clip-overlay.mp4 --bones

The bar is always drawn. The body points ("joints") are drawn unless --no-joints is given,
and the lines between them ("bones") only with --bones.

The result shows the lifter, like the clip does: keep it out of git and off shared storage.
"""

import argparse
import itertools
import json
import sys
from pathlib import Path

import cv2

from vision_worker.smooth import POSE_SURE
from vision_worker.video import VideoError, VideoReader

# A phone clip is 1080p or 4K; this is enough to judge a circle and keeps the file small.
MAX_SIDE = 960

# BGR. Magenta stands out against every plate colour in use.
BAR_COLOUR = (255, 0, 255)
PATH_COLOUR = (255, 160, 255)
JOINT_COLOUR = (0, 255, 255)
BONE_COLOUR = (255, 200, 0)
MISSING_COLOUR = (0, 0, 255)
FAINT = 0.45  # how bright a body point the model is unsure of is drawn, and a bone that ends in one

# The speed shown is for judging a track by eye, not a result: it takes the circle for a plate
# of 450 mm, which a smaller plate or a circle of the wrong size makes wrong by as much.
PLATE_RADIUS_M = 0.225
SPEED_FRAMES = 2  # frames to either side between which the rise is taken

# Lines between the contract's body points. The nose has no line: the contract has no neck point to join it to.
BONES = (
    ("left_shoulder", "right_shoulder"),
    ("left_shoulder", "left_elbow"),
    ("left_elbow", "left_wrist"),
    ("right_shoulder", "right_elbow"),
    ("right_elbow", "right_wrist"),
    ("left_shoulder", "left_hip"),
    ("right_shoulder", "right_hip"),
    ("left_hip", "right_hip"),
    ("left_hip", "left_knee"),
    ("left_knee", "left_ankle"),
    ("right_hip", "right_knee"),
    ("right_knee", "right_ankle"),
)
# python -m vision_worker.overlay ~/CoachVisionData/squats/squat_12.mp4 ~/CoachVisionData/raw-output/heavy/squat_12.json ~/CoachVisionData/new_outputs/heavy/squat_12-overlay.mp4 --bones


class OverlayError(Exception):
    """The raw output can't be drawn over this video."""


def _parser():
    parser = argparse.ArgumentParser(
        prog="vision_worker.overlay",
        description="Draw a raw output's bar over its video, with or without the body points and their joining lines.",
    )
    parser.add_argument("video", help="the video the raw output was made from")
    parser.add_argument("raw_output", help="the worker's JSON for that video")
    parser.add_argument("out", help="where to write the overlay video (.mp4)")
    parser.add_argument(
        "--joints",
        action=argparse.BooleanOptionalAction,
        default=True,
        help="draw the body points (default: on)",
    )
    parser.add_argument(
        "--bones",
        action=argparse.BooleanOptionalAction,
        default=False,
        help="draw lines between the body points (default: off)",
    )
    return parser


def _faint(colour):
    return tuple(round(FAINT * channel) for channel in colour)


def vertical_speeds(frames):
    """How fast the bar rises in each frame, in metres a second (falling is below zero); None where it can't be said.

    The rise is taken between the furthest frames with a position up to SPEED_FRAMES before
    and after, in the frame's own radii, with the radius taken as PLATE_RADIUS_M.
    """
    speeds = []
    for i, frame in enumerate(frames):
        bar = frame["bar"]
        around = [
            other for other in frames[max(i - SPEED_FRAMES, 0) : i + SPEED_FRAMES + 1] if other["bar"] is not None
        ]
        if bar is None or len(around) < 2 or around[-1]["t"] <= around[0]["t"]:
            speeds.append(None)
            continue
        risen = around[0]["bar"]["y"] - around[-1]["bar"]["y"]  # the picture's y grows downwards
        speeds.append(risen / bar["radiusPx"] * PLATE_RADIUS_M / (around[-1]["t"] - around[0]["t"]))
    return speeds


def draw(image, frame, shrink, path, *, joints=True, bones=False, speed=None):
    """Draw one frame's detections on `image`, which is the video frame scaled by `shrink`.

    `path` collects the bar's centres from frame to frame. The bar is always drawn;
    `joints` and `bones` switch the body points and the lines between them. A point the model is
    unsure of (confidence under POSE_SURE) is drawn smaller and fainter, and so is a bone that
    ends in one. `speed` (metres a
    second, rising above zero) is written beside the bar's label when given.
    """
    thickness = max(round(max(image.shape[:2]) / 320), 2)
    points = {
        name: (round(point["x"] * shrink), round(point["y"] * shrink)) for name, point in (frame["pose"] or {}).items()
    }
    sure = {name: point["confidence"] >= POSE_SURE for name, point in (frame["pose"] or {}).items()}
    # Faint first, so a sure bone or point lying across an unsure one stays in full.
    for full in (False, True):
        if bones:
            for start, end in BONES:
                # A bone is drawn only between two points the model reported, never towards a guess.
                if start in points and end in points and (sure[start] and sure[end]) == full:
                    colour = BONE_COLOUR if full else _faint(BONE_COLOUR)
                    cv2.line(
                        image,
                        points[start],
                        points[end],
                        colour,
                        thickness if full else max(thickness - 1, 1),
                        cv2.LINE_AA,
                    )
        if joints:
            for name, centre in points.items():
                if sure[name] == full:
                    colour = JOINT_COLOUR if full else _faint(JOINT_COLOUR)
                    cv2.circle(image, centre, thickness + 1 if full else thickness, colour, -1, cv2.LINE_AA)

    bar = frame["bar"]
    if bar is not None:
        path.append((round(bar["x"] * shrink), round(bar["y"] * shrink)))
    for earlier, later in itertools.pairwise(path):
        cv2.line(image, earlier, later, PATH_COLOUR, 1, cv2.LINE_AA)

    scale = max(image.shape[:2]) / 900
    if bar is None:
        label, colour = f"{frame['index']}  no bar", MISSING_COLOUR
    else:
        cv2.circle(image, path[-1], max(round(bar["radiusPx"] * shrink), 1), BAR_COLOUR, thickness, cv2.LINE_AA)
        cv2.circle(image, path[-1], thickness + 2, BAR_COLOUR, -1, cv2.LINE_AA)
        label, colour = f"{frame['index']}  bar {bar['confidence']:.2f}", BAR_COLOUR
        if speed is not None:
            label += f"  {speed:+.2f} m/s"
    origin = (round(12 * scale), round(36 * scale))
    cv2.putText(image, label, origin, cv2.FONT_HERSHEY_SIMPLEX, scale, (0, 0, 0), thickness + 2, cv2.LINE_AA)
    cv2.putText(image, label, origin, cv2.FONT_HERSHEY_SIMPLEX, scale, colour, thickness, cv2.LINE_AA)


def render(video, frames, out, *, joints=True, bones=False):
    """Write `video` with `frames` (the raw output's list) drawn on it. Returns the number of frames written."""
    out = Path(out)
    out.parent.mkdir(parents=True, exist_ok=True)
    # Written under another name first, so a refused or failed run leaves no half-made file behind.
    partial = out.with_name(out.stem + ".partial" + out.suffix)
    writer, path, written = None, [], 0
    speeds = vertical_speeds(frames)
    try:
        with VideoReader(video) as reader:
            for frame in reader:
                if frame.index >= len(frames):
                    raise OverlayError(f"the video has more frames than the raw output ({len(frames)})")
                image = frame.image
                shrink = min(1.0, MAX_SIDE / max(image.shape[:2]))
                if shrink < 1:
                    height, width = image.shape[:2]
                    size = (width * MAX_SIDE // max(width, height), height * MAX_SIDE // max(width, height))
                    image = cv2.resize(image, size, interpolation=cv2.INTER_AREA)
                if writer is None:
                    # Written at the video's nominal rate: a clip with an uneven frame rate plays slightly off-speed.
                    writer = cv2.VideoWriter(
                        str(partial), cv2.VideoWriter_fourcc(*"mp4v"), reader.nominal_rate, image.shape[1::-1]
                    )
                    if not writer.isOpened():
                        raise OverlayError(f"could not write a video to {out}")
                draw(image, frames[frame.index], shrink, path, joints=joints, bones=bones, speed=speeds[frame.index])
                writer.write(image)
                written += 1
        if written != len(frames):
            raise OverlayError(f"the video has {written} frames but the raw output has {len(frames)}")
    except BaseException:
        if writer is not None:
            writer.release()
        partial.unlink(missing_ok=True)
        raise
    writer.release()
    partial.replace(out)
    return written


def main(argv=None):
    parser = _parser()
    args = parser.parse_args(argv)
    if Path(args.out).resolve() in {Path(args.video).resolve(), Path(args.raw_output).resolve()}:
        parser.error("the overlay must not be written over its inputs")
    try:
        try:
            frames = json.loads(Path(args.raw_output).read_text())["frames"]
        except (OSError, ValueError, KeyError, TypeError) as error:
            raise OverlayError(f"not a raw output file: {args.raw_output} ({error})") from error
        written = render(args.video, frames, args.out, joints=args.joints, bones=args.bones)
    except (VideoError, OverlayError) as error:
        print(f"error: {error}", file=sys.stderr)
        return 1
    print(f"{written} frames written to {args.out}", file=sys.stderr)
    return 0


if __name__ == "__main__":
    sys.exit(main())
