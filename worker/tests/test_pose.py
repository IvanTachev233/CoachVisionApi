from types import SimpleNamespace

import mediapipe
import numpy as np
import pytest

from vision_worker import models
from vision_worker.pose import (
    LANDMARK_INDEX,
    PoseLandmarkerEstimator,
    named_landmarks,
    pose_model_name,
)


def landmark(x=0.5, y=0.25, visibility=0.9, presence=0.8):
    return SimpleNamespace(x=x, y=y, z=0.0, visibility=visibility, presence=presence)


def full_body(**overrides):
    points = [landmark() for _ in range(33)]
    for index, value in overrides.items():
        points[int(index.removeprefix("i"))] = value
    return points


def test_names_match_the_contract_and_the_model_numbering():
    assert LANDMARK_INDEX == {
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


def test_landmarks_are_scaled_to_pixels():
    pose = named_landmarks(full_body(i24=landmark(x=0.75, y=0.5)), width=1080, height=1920)

    assert set(pose) == set(LANDMARK_INDEX)
    assert pose["nose"] == {"x": 540.0, "y": 480.0, "confidence": 0.8}
    assert pose["right_hip"] == {"x": 810.0, "y": 960.0, "confidence": 0.8}


def test_confidence_is_the_lower_of_visibility_and_presence_within_0_and_1():
    pose = named_landmarks(
        full_body(
            i11=landmark(visibility=0.3, presence=0.95),
            i12=landmark(visibility=1.2, presence=1.5),
            i13=landmark(visibility=-0.1, presence=0.5),
            i14=landmark(visibility=None, presence=0.6),
        ),
        width=100,
        height=100,
    )

    assert pose["left_shoulder"]["confidence"] == pytest.approx(0.3)
    assert pose["right_shoulder"]["confidence"] == 1.0
    assert pose["left_elbow"]["confidence"] == 0.0
    assert pose["right_elbow"]["confidence"] == pytest.approx(0.6)


def test_a_point_the_model_does_not_report_is_left_out():
    pose = named_landmarks(
        full_body(
            i0=landmark(x=float("nan")),
            i15=landmark(y=None),
            i16=landmark(visibility=None, presence=None),
        ),
        width=100,
        height=100,
    )

    assert "nose" not in pose
    assert "left_wrist" not in pose
    assert "right_wrist" not in pose
    assert "left_hip" in pose


def test_no_usable_point_means_no_pose():
    nothing = [landmark(x=None) for _ in range(33)]

    assert named_landmarks(nothing, width=100, height=100) is None


@pytest.mark.parametrize("variant", ["full", "heavy"])
def test_model_name_carries_variant_and_library_version(variant):
    assert pose_model_name(variant) == f"mediapipe-pose-landmarker-{variant}@{mediapipe.__version__}"


@pytest.mark.skipif(not models.is_available("full"), reason="pose_landmarker_full.task is not in the model folder")
def test_real_model_reports_no_pose_on_an_empty_frame():
    blank = np.full((480, 270, 3), 128, dtype=np.uint8)

    estimator = PoseLandmarkerEstimator(models.model_path("full"))
    try:
        # The same time twice: the task needs increasing timestamps, so the estimator must cope.
        assert estimator.detect(blank, 0.0) is None
        assert estimator.detect(blank, 0.0) is None
        assert estimator.detect(blank, 0.0333) is None
    finally:
        estimator.close()
