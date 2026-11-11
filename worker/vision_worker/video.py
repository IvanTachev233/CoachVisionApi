"""Decodes a clip frame by frame: upright, and with each frame's own timestamp."""

import math
from dataclasses import dataclass
from pathlib import Path

import cv2
import numpy as np

# Used only when the file gives neither timestamps nor a frame rate.
FALLBACK_FRAME_RATE = 30.0


class VideoError(Exception):
    """The file is missing, is not a video, or has no frame that can be decoded."""


@dataclass(frozen=True)
class Frame:
    index: int
    t: float  # seconds from the start of the video
    image: np.ndarray  # BGR, rotation applied


def _open(path):
    if not path.is_file():
        raise VideoError(f"video not found: {path}")
    # The FFmpeg backend on every platform, so a laptop and the worker image decode alike.
    capture = cv2.VideoCapture(str(path), cv2.CAP_FFMPEG)
    if not capture.isOpened():
        raise VideoError(f"not a readable video: {path}")
    # Phones store sideways pixels plus a rotation tag. This is FFmpeg's default; set so it can't drift.
    capture.set(cv2.CAP_PROP_ORIENTATION_AUTO, 1)
    return capture


def count_frames(path):
    """How many frames a VideoReader yields for this file.

    The count in a file's header can be wrong, so the frames are stepped through, without
    turning them into pictures.
    """
    capture = _open(Path(path))
    try:
        count = 0
        while capture.grab():
            count += 1
        return count
    finally:
        capture.release()


class VideoReader:
    """Iterates over a video's frames. `width` and `height` are the upright size."""

    def __init__(self, path):
        self.path = Path(path)
        self._capture = _open(self.path)
        self.width = int(self._capture.get(cv2.CAP_PROP_FRAME_WIDTH))
        self.height = int(self._capture.get(cv2.CAP_PROP_FRAME_HEIGHT))
        rate = self._capture.get(cv2.CAP_PROP_FPS)
        self._nominal_step = 1.0 / (rate if math.isfinite(rate) and rate > 0 else FALLBACK_FRAME_RATE)
        self._times = []

    @property
    def nominal_rate(self):
        """The frame rate the file declares; real frame times can be uneven."""
        return 1.0 / self._nominal_step

    def __enter__(self):
        return self

    def __exit__(self, *exc_info):
        self.close()

    def close(self):
        self._capture.release()

    def __iter__(self):
        return self.frames()

    def frames(self, only=None):
        """Every frame in order, or just those whose number is in `only`.

        Frames that aren't asked for are stepped over without being turned into pictures,
        which is the slow part; they still count, and keep their number and time.
        """
        while True:
            if not self._capture.grab():
                return
            # Phone video often has a variable frame rate, so the time comes from the frame, not its number.
            t = self._capture.get(cv2.CAP_PROP_POS_MSEC) / 1000.0
            if not math.isfinite(t) or t < 0:
                t = 0.0
            if self._times and t <= self._times[-1]:
                t = self._times[-1] + self._nominal_step
            index = len(self._times)
            if only is not None and index not in only:
                self._times.append(t)
                continue
            ok, image = self._capture.retrieve()
            if not ok:
                return
            self.height, self.width = image.shape[:2]
            self._times.append(t)
            yield Frame(index=index, t=t, image=image)

    def summary(self):
        """The contract's `video` block, for the frames decoded so far."""
        count = len(self._times)
        if count == 0:
            raise VideoError(f"no frame could be decoded: {self.path}")
        step = self._nominal_step
        if count > 1 and self._times[-1] > self._times[0]:
            step = (self._times[-1] - self._times[0]) / (count - 1)
        return {
            "width": self.width,
            "height": self.height,
            "frameRate": 1.0 / step,
            "durationS": self._times[-1] + step,
            "frameCount": count,
        }
