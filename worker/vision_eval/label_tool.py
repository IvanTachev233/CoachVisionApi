"""Click the plate in a few frames of each clip, to get the labels the worker is scored against.

    python -m vision_eval.label_tool /path/to/clips

Each clip shows 5 frames spread over it. On each: click the plate's centre, then its top or
bottom edge; Space keeps it. Finished clips are saved to plate-labels.csv beside the clips,
and a later run carries on with the clips that have no labels yet.

Frames are only shown in a window on this machine. Nothing but numbers is written.
"""

import argparse
import math
import sys
from dataclasses import dataclass
from pathlib import Path

import cv2
import numpy as np

from vision_eval.labels import (
    LabelError,
    PlateLabel,
    labelled_files,
    pick_frames,
    read_labels,
    replace_clip,
    write_labels,
)
from vision_worker.video import VideoError, VideoReader, count_frames

CLIP_TYPES = {".mp4", ".mov", ".m4v"}
WINDOW_TITLE = "Plate labels"

# Height of the strip of text above the picture, in window pixels.
BANNER = 66
# BGR. Magenta stands out against every plate colour in use.
MARK_COLOUR = (255, 0, 255)
TEXT_COLOUR = (255, 255, 255)
# A second click this close to the first, in window pixels, is a slip, not a rim.
MIN_RADIUS = 4

ACCEPT_KEYS = {13, 10, 32}  # Return, Enter, Space
RESET_KEYS = {ord("r")}
HIDDEN_KEYS = {ord("h")}
QUIT_KEYS = {ord("q"), 27}  # q, Esc

INSTRUCTIONS = """\
On each frame:
  1. Click the centre of the plate nearest the camera.
     If a smaller plate sits in front of the big one, label the big one.
  2. Click the top or bottom edge of that plate.
  3. Space (or Enter) keeps it and moves on.
If that plate can't be seen in a frame, press h, then Space.
Other keys: r starts the frame again, q (or Esc) saves the finished clips and stops.
If the first click only brings the window to the front, click again.
"""


@dataclass(frozen=True)
class View:
    """How the window shows a frame: shrunk by `scale`, below a banner `top` pixels high."""

    scale: float
    top: int

    def to_video(self, x, y):
        """A window pixel as a point in the video's pixels."""
        return (x / self.scale, (y - self.top) / self.scale)

    def to_window(self, x, y):
        """A point in the video's pixels as the window pixel it is shown at."""
        return (round(x * self.scale), round(y * self.scale) + self.top)


def fit(image, max_width, max_height):
    """`image` shrunk to fit the given size, and the scale used; never enlarged."""
    height, width = image.shape[:2]
    scale = min(1.0, max_width / width, max_height / height)
    if scale == 1.0:
        return image, 1.0
    size = (max(round(width * scale), 1), max(round(height * scale), 1))
    return cv2.resize(image, size, interpolation=cv2.INTER_AREA), scale


class FrameLabeller:
    """One frame's labelling: takes clicks, mouse moves and keys; knows nothing about windows."""

    def __init__(self, image, view, heading):
        self._image, self._view, self._heading = image, view, heading
        self._centre = self._rim = self._mouse = None  # window pixels
        self._hidden = False

    def click(self, x, y):
        if y < self._view.top:
            return
        self._hidden = False
        if self._centre is None or self._rim is not None:
            # The first click, or a click after the circle was complete: start from here.
            self._centre, self._rim = (x, y), None
        elif math.dist(self._centre, (x, y)) >= MIN_RADIUS:
            self._rim = (x, y)

    def move(self, x, y):
        self._mouse = (x, y)

    def key(self, code):
        """What a key press asks for: "accept", "quit", or None.

        Only Space or Enter moves on, and only once the frame has an answer, so a held key
        can't run through the frames.
        """
        if code in ACCEPT_KEYS:
            return "accept" if self.result() is not None else None
        if code in RESET_KEYS:
            self._centre = self._rim = None
            self._hidden = False
        elif code in HIDDEN_KEYS:
            self._centre = self._rim = None
            self._hidden = True
        elif code in QUIT_KEYS:
            return "quit"
        return None

    def result(self):
        """The frame's answer: the centre and rim clicks in the video's pixels, (None, None) for a
        plate marked hidden, or None while there is no answer yet."""
        if self._hidden:
            return None, None
        if self._rim is None:
            return None
        return self._view.to_video(*self._centre), self._view.to_video(*self._rim)

    def render(self):
        """What the window shows: the banner, the frame, and the marks made so far."""
        height, width = self._image.shape[:2]
        canvas = np.zeros((height + self._view.top, width, 3), dtype=np.uint8)
        canvas[self._view.top :] = self._image

        if self._hidden:
            step = "Plate hidden. Space: keep that      or click the plate"
        elif self._centre is None:
            step = "1. Click the plate's centre"
        elif self._rim is None:
            step = "2. Click its top or bottom edge"
        else:
            step = "Space: keep it      or click again to redo"
        for line, text in enumerate((self._heading, step, "r: start again   h: plate hidden   q: save and quit")):
            cv2.putText(canvas, text, (8, 18 + 20 * line), cv2.FONT_HERSHEY_SIMPLEX, 0.45, TEXT_COLOUR, 1, cv2.LINE_AA)

        if self._centre is not None:
            cv2.drawMarker(canvas, self._centre, MARK_COLOUR, cv2.MARKER_CROSS, 12, 1)
            if self._rim is not None:
                cv2.circle(canvas, self._centre, round(math.dist(self._centre, self._rim)), MARK_COLOUR, 2)
                cv2.circle(canvas, self._rim, 3, MARK_COLOUR, -1)
            elif self._mouse is not None and self._mouse[1] >= self._view.top:
                # Until the second click the circle follows the mouse, to line it up with the rim.
                cv2.circle(canvas, self._centre, round(math.dist(self._centre, self._mouse)), MARK_COLOUR, 1)
        return canvas


class CvWindow:
    """The real window, an OpenCV one. Kept thin: everything worth testing is outside it."""

    def __init__(self, title):
        self._title = title
        cv2.namedWindow(title, cv2.WINDOW_AUTOSIZE)

    def on_mouse(self, click, move):
        def handle(event, x, y, flags, userdata):
            if event == cv2.EVENT_LBUTTONDOWN:
                click(x, y)
            elif event == cv2.EVENT_MOUSEMOVE:
                move(x, y)

        cv2.setMouseCallback(self._title, handle)

    def show(self, image):
        cv2.imshow(self._title, image)

    def notice(self, text):
        """Show a line of text while the next clip loads."""
        canvas = np.zeros((BANNER, 480, 3), dtype=np.uint8)
        cv2.putText(canvas, text, (8, 38), cv2.FONT_HERSHEY_SIMPLEX, 0.5, TEXT_COLOUR, 1, cv2.LINE_AA)
        cv2.imshow(self._title, canvas)
        cv2.waitKey(1)

    def wait_key(self, milliseconds):
        code = cv2.waitKey(milliseconds)
        return code & 0xFF if code >= 0 else -1

    def is_closed(self):
        try:
            return cv2.getWindowProperty(self._title, cv2.WND_PROP_VISIBLE) < 1
        except cv2.error:
            return True

    def close(self):
        try:
            cv2.destroyWindow(self._title)
        except cv2.error:
            pass  # already closed by hand
        cv2.waitKey(1)


def label_clip(window, path, *, wanted, max_width, max_height, position):
    """Ask for the plate on `wanted` frames of one clip. Returns its labels, or None if the person stopped."""
    path = Path(path)
    picks = pick_frames(count_frames(path), wanted)
    shots = []
    with VideoReader(path) as reader:
        for frame in reader.frames(only=set(picks)):
            shots.append((frame.index, frame.t, *fit(frame.image, max_width, max_height)))
            if len(shots) == len(picks):
                break
        width, height = reader.width, reader.height
    if not shots:
        raise VideoError(f"no frame could be decoded: {path}")

    labels = []
    for number, (index, t, shown, scale) in enumerate(shots, start=1):
        heading = f"{path.name}  frame {number}/{len(shots)}  {position}"
        tool = FrameLabeller(shown, View(scale=scale, top=BANNER), heading)
        window.on_mouse(tool.click, tool.move)
        while True:
            window.show(tool.render())
            action = tool.key(window.wait_key(20))
            if action == "quit" or window.is_closed():
                return None
            if action == "accept":
                centre, rim = tool.result()
                labels.append(
                    PlateLabel(
                        file=path.name, frame=index, t=t, video_width=width, video_height=height, centre=centre, rim=rim
                    )
                )
                break
    return labels


def _at_least_one(value):
    number = int(value)
    if number < 1:
        raise argparse.ArgumentTypeError("must be 1 or more")
    return number


def _parser():
    parser = argparse.ArgumentParser(
        prog="vision_eval.label_tool",
        description="Click the plate in a few frames of each clip and save the positions as plate labels.",
    )
    parser.add_argument("clips", help="folder with the clips")
    parser.add_argument("--out", help="the label file (default: plate-labels.csv in the clips folder)")
    parser.add_argument("--frames", type=_at_least_one, default=5, help="frames to label per clip (default: 5)")
    parser.add_argument(
        "--only", nargs="+", default=[], metavar="FILE", help="label just these clips, again if they have labels"
    )
    parser.add_argument("--max-height", type=int, default=760, help="tallest picture in screen pixels (default: 760)")
    parser.add_argument("--max-width", type=int, default=1400, help="widest picture in screen pixels (default: 1400)")
    return parser


def main(argv=None):
    args = _parser().parse_args(argv)
    folder = Path(args.clips)
    out = Path(args.out) if args.out else folder / "plate-labels.csv"
    clips = sorted(p for p in folder.iterdir() if p.suffix.lower() in CLIP_TYPES) if folder.is_dir() else []
    if not clips:
        print(f"error: no clips found in {folder}", file=sys.stderr)
        return 1
    names = [clip.name for clip in clips]
    unknown = [name for name in args.only if name not in names]
    if unknown:
        print(f"error: not in {folder}: {', '.join(unknown)}", file=sys.stderr)
        return 1
    try:
        labels = read_labels(out)
    except LabelError as error:
        print(f"error: {error}", file=sys.stderr)
        return 1

    done = labelled_files(labels)
    todo = [clip for clip in clips if (clip.name in args.only if args.only else clip.name not in done)]
    failed = False
    window = None
    if todo:
        print(f"{len(todo)} clips to label, {args.frames} frames each.\n{INSTRUCTIONS}")
    try:
        for clip in todo:
            if window is None:
                window = CvWindow(WINDOW_TITLE)
            window.notice(f"Loading {clip.name} ...")
            try:
                new = label_clip(
                    window,
                    clip,
                    wanted=args.frames,
                    max_width=args.max_width,
                    max_height=args.max_height,
                    position=f"clip {names.index(clip.name) + 1}/{len(clips)}",
                )
            except VideoError as error:
                print(f"error: {error}", file=sys.stderr)
                failed = True
                continue
            if new is None:
                break
            # Saved clip by clip, so stopping never loses a finished clip.
            labels = replace_clip(labels, clip.name, new)
            write_labels(out, labels)
    finally:
        if window is not None:
            window.close()

    count = len(labelled_files(labels) & set(names))
    rest = "" if count == len(clips) else "; run the same command to carry on"
    print(f"{count} of {len(clips)} clips labelled in {out}{rest}")
    return 1 if failed else 0


if __name__ == "__main__":
    sys.exit(main())
