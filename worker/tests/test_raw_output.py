import json
import re

import pytest
from conftest import CONTRACTS

from vision_worker.bar import BarDetection
from vision_worker.raw_output import (
    SCHEMA_VERSION,
    build_document,
    is_job_id,
    new_job_id,
)

JOB_ID = "job_01JC8YX4D2F7H1K9M3P5Q8S0TV"
VIDEO = {"width": 1080, "height": 1920, "frameRate": 30.0, "durationS": 0.1, "frameCount": 3}
POSE = {"right_hip": {"x": 455.04321, "y": 1010.5, "confidence": 0.98765}}


def document(frames):
    return build_document(
        job_id=JOB_ID,
        pose_model="mediapipe-pose-landmarker-full@1.2.2",
        video=VIDEO,
        frames=frames,
    )


def test_contract_example_passes_the_validator(raw_output_validator):
    example = json.loads((CONTRACTS / "examples" / "three-frames.json").read_text())

    assert list(raw_output_validator.iter_errors(example)) == []


def test_document_with_detections_and_gaps_validates(raw_output_validator):
    result = document(
        [
            (0, 0.0, BarDetection(x=402.5, y=611.0, radius_px=141.2, confidence=0.93), POSE),
            (1, 0.0333, None, POSE),
            (2, 0.0667, BarDetection(x=402.9, y=616.4, radius_px=140.8, confidence=0.91), None),
        ]
    )

    assert list(raw_output_validator.iter_errors(result)) == []
    assert result["schemaVersion"] == SCHEMA_VERSION == "1.0"
    assert result["jobId"] == JOB_ID
    assert result["worker"]["poseModel"] == "mediapipe-pose-landmarker-full@1.2.2"
    assert result["worker"]["barDetector"].startswith("opencv-")
    assert result["frames"][0]["bar"] == {"x": 402.5, "y": 611.0, "radiusPx": 141.2, "confidence": 0.93}
    assert result["frames"][1]["bar"] is None
    assert result["frames"][2]["pose"] is None


def test_document_with_no_frames_detected_validates(raw_output_validator):
    result = document([(0, 0.0, None, None), (1, 0.0333, None, None)])

    assert list(raw_output_validator.iter_errors(result)) == []


def test_numbers_are_rounded_for_a_compact_file():
    result = document([(0, 0.033366666, BarDetection(402.512345, 611.0, 141.23456, 0.934567), POSE)])

    frame = result["frames"][0]
    assert frame["t"] == 0.0334
    assert frame["bar"] == {"x": 402.51, "y": 611.0, "radiusPx": 141.23, "confidence": 0.935}
    assert frame["pose"]["right_hip"] == {"x": 455.04, "y": 1010.5, "confidence": 0.988}


def test_a_broken_document_fails_the_validator(raw_output_validator):
    result = document([(0, 0.0, None, None)])
    result["frames"][0]["bar"] = {"x": 1.0, "y": 2.0, "radiusPx": 0, "confidence": 1.4}

    assert list(raw_output_validator.iter_errors(result)) != []


def test_new_job_ids_match_the_contract_and_differ():
    first, second = new_job_id(), new_job_id()

    assert re.fullmatch(r"job_[0-9A-HJKMNP-TV-Z]{26}", first)
    assert is_job_id(first)
    assert first != second


@pytest.mark.parametrize("value", ["", "job_123", "JOB_01JC8YX4D2F7H1K9M3P5Q8S0TV", "job_01JC8YX4D2F7H1K9M3P5Q8S0TU"])
def test_other_strings_are_not_job_ids(value):
    assert not is_job_id(value)
