"""Body landmarks from the MediaPipe Pose Landmarker task, as the contract's named points in pixels."""

import math

import cv2
import mediapipe as mp
from mediapipe.tasks.python import BaseOptions, vision

from vision_worker.models import ModelError

# The contract's names against the model's landmark numbers, so nothing downstream depends on the numbering.
LANDMARK_INDEX = {
    "nose": 0,
    "left_shoulder": 11,
    "right_shoulder": 12,
    "left_elbow": 13,
    "right_elbow": 14,
    "left_wrist": 15,
    "right_wrist": 16,
    "left_hip": 23,
    "right_hip": 24,
    "left_knee": 25,
    "right_knee": 26,
    "left_ankle": 27,
    "right_ankle": 28,
}

# The model works on a 256-pixel crop of the person, so a 4K frame only costs time.
MAX_SIDE = 1280


def pose_model_name(variant):
    return f"mediapipe-pose-landmarker-{variant}@{mp.__version__}"


def named_landmarks(landmarks, width, height):
    """One person's landmarks (normalised to the frame) as named pixel points, or None if none is usable.

    Confidence is the lower of the model's visibility and presence scores. Only x and y are kept:
    the model card says z is not a metric depth.
    """
    pose = {}
    for name, index in LANDMARK_INDEX.items():
        point = landmarks[index]
        scores = [s for s in (point.visibility, point.presence) if s is not None and math.isfinite(s)]
        if point.x is None or point.y is None or not scores:
            continue
        if not (math.isfinite(point.x) and math.isfinite(point.y)):
            continue
        pose[name] = {
            "x": point.x * width,
            "y": point.y * height,
            "confidence": min(max(min(scores), 0.0), 1.0),
        }
    return pose or None


class PoseLandmarkerEstimator:
    """The Pose Landmarker in VIDEO mode, tracking one person."""

    def __init__(self, model_file):
        options = vision.PoseLandmarkerOptions(
            base_options=BaseOptions(model_asset_path=str(model_file)),
            running_mode=vision.RunningMode.VIDEO,
            num_poses=1,
        )
        try:
            self._landmarker = vision.PoseLandmarker.create_from_options(options)
        except (RuntimeError, ValueError) as error:
            raise ModelError(f"could not load the pose model {model_file}: {error}") from error
        self._last_ms = -1

    def detect(self, image, t):
        """Landmarks for one BGR frame at `t` seconds, in that frame's pixels, or None when no body is found."""
        height, width = image.shape[:2]
        shrink = MAX_SIDE / max(width, height)
        if shrink < 1:
            image = cv2.resize(image, None, fx=shrink, fy=shrink, interpolation=cv2.INTER_AREA)
        frame = mp.Image(image_format=mp.ImageFormat.SRGB, data=cv2.cvtColor(image, cv2.COLOR_BGR2RGB))
        # VIDEO mode needs whole milliseconds that always increase.
        timestamp_ms = max(round(t * 1000), self._last_ms + 1)
        self._last_ms = timestamp_ms
        result = self._landmarker.detect_for_video(frame, timestamp_ms)
        if not result.pose_landmarks:
            return None
        return named_landmarks(result.pose_landmarks[0], width, height)

    def close(self):
        self._landmarker.close()
