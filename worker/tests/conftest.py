"""Shared test helpers: the raw-output schema and synthetic clips.

No real clip is used here. A synthetic scene is a textured background with a
static plate on a rack (a distractor) and, optionally, a loaded plate that
moves down and up like a squat. The lifter is faked: `FakePose` reports
shoulders that travel with the bar, as they do in a back squat.
"""

import json
import math
from pathlib import Path

import cv2
import numpy as np
import pytest
from jsonschema import Draft202012Validator

CONTRACTS = Path(__file__).resolve().parents[2] / "contracts" / "internal" / "raw-output"

# Where the faked shoulders sit relative to the plate's centre, in pixels.
SHOULDER_OFFSET = (-70.0, 12.0)


@pytest.fixture(scope="session")
def raw_output_validator():
    schema = json.loads((CONTRACTS / "v1.json").read_text())
    Draft202012Validator.check_schema(schema)
    return Draft202012Validator(schema)


def squat_path(frame_count, top_y, depth, reps=2):
    """Plate-centre heights for a clip: stand, then `reps` smooth descents and ascents."""
    still = max(frame_count // 8, 1)
    moving = frame_count - 2 * still
    ys = []
    for i in range(frame_count):
        phase = min(max(i - still, 0), moving) / moving
        ys.append(top_y + depth * 0.5 * (1 - math.cos(2 * math.pi * reps * phase)))
    return ys


def render_scene(width, height, plate=None, seed=7):
    """One BGR frame. `plate` is (cx, cy, rx, ry) or None for a scene with no loaded plate."""
    rng = np.random.default_rng(seed)
    noise = rng.integers(90, 150, size=(height // 8 + 1, width // 8 + 1), dtype=np.uint8)
    image = cv2.resize(noise, (width, height), interpolation=cv2.INTER_CUBIC)
    image = cv2.cvtColor(image, cv2.COLOR_GRAY2BGR)

    # A rack upright and a plate stored on it: round, plate-sized, and never moving.
    upright_x = int(width * 0.82)
    cv2.rectangle(image, (upright_x, 0), (upright_x + width // 40, height), (60, 60, 60), -1)
    stored_r = int(width * 0.085)
    cv2.circle(image, (upright_x, int(height * 0.3)), stored_r, (35, 35, 35), -1)
    cv2.circle(image, (upright_x, int(height * 0.3)), stored_r // 4, (170, 170, 170), -1)

    if plate is not None:
        cx, cy, rx, ry = plate
        centre = (round(cx), round(cy))
        cv2.ellipse(image, centre, (int(rx), int(ry)), 0, 0, 360, (40, 110, 45), -1, cv2.LINE_AA)
        # The raised ring and the hub a real plate has inside its rim.
        cv2.ellipse(image, centre, (int(rx * 0.8), int(ry * 0.8)), 0, 0, 360, (60, 135, 65), 2, cv2.LINE_AA)
        cv2.ellipse(image, centre, (int(rx * 0.22), int(ry * 0.22)), 0, 0, 360, (185, 185, 185), -1, cv2.LINE_AA)
    return image


def squat_scenes(width, height, frame_count, radius, *, plate=True, squash=1.0, reps=2):
    """Frames and plate centres for a synthetic squat. `squash` narrows the plate, as a diagonal view does."""
    cx = width * 0.42
    ys = squat_path(frame_count, top_y=height * 0.3, depth=radius * 2.6, reps=reps)
    frames = []
    for i, cy in enumerate(ys):
        shape = (cx, cy, radius * squash, radius) if plate else None
        frames.append(render_scene(width, height, shape, seed=7 + i % 3))
    return frames, [(cx, cy) for cy in ys]


def fake_shoulders(centre, frame_index):
    """Mid-shoulder point for a plate centre, with a little landmark jitter."""
    jitter = 1.5 * math.sin(frame_index * 1.7)
    return (centre[0] + SHOULDER_OFFSET[0] + jitter, centre[1] + SHOULDER_OFFSET[1] - jitter)


def fake_pose(centre, frame_index, half_width=30.0):
    """Named landmarks in pixels for a lifter whose shoulders travel with the bar."""
    mid_x, mid_y = fake_shoulders(centre, frame_index)

    def point(dx, dy):
        return {"x": mid_x + dx, "y": mid_y + dy, "confidence": 0.9}

    return {
        "left_shoulder": point(half_width, 0.0),
        "right_shoulder": point(-half_width, 0.0),
        "left_hip": point(half_width * 0.8, 150.0),
        "right_hip": point(-half_width * 0.8, 150.0),
    }


class FakePose:
    """Stands in for the Pose Landmarker: replays one pose per frame, in order."""

    def __init__(self, poses):
        self._poses = list(poses)
        self._next = 0
        self.closed = False

    def detect(self, image, t):
        pose = self._poses[self._next]
        self._next += 1
        return pose

    def close(self):
        self.closed = True


def write_clip(path, frames, fps=30.0):
    """Write BGR frames to an MP4 with OpenCV's bundled encoder."""
    height, width = frames[0].shape[:2]
    writer = cv2.VideoWriter(str(path), cv2.VideoWriter_fourcc(*"mp4v"), fps, (width, height))
    if not writer.isOpened():
        raise RuntimeError("OpenCV could not open an MP4 writer")
    for frame in frames:
        writer.write(frame)
    writer.release()
    return path
