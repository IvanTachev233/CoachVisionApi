"""Builds the raw-output document the contract describes (contracts/internal/raw-output/v1.json)."""

import re
import secrets
import time

from vision_worker import __version__
from vision_worker.bar import BAR_DETECTOR

SCHEMA_VERSION = "1.0"

_CROCKFORD = "0123456789ABCDEFGHJKMNPQRSTVWXYZ"
_JOB_ID = re.compile(r"job_[0-9A-HJKMNP-TV-Z]{26}")


def is_job_id(value):
    return _JOB_ID.fullmatch(value) is not None


def new_job_id():
    """A job id in the contract's form (`job_` + ULID), for runs outside the API."""
    value = (time.time_ns() // 1_000_000) << 80 | secrets.randbits(80)
    digits = []
    for _ in range(26):
        digits.append(_CROCKFORD[value & 31])
        value >>= 5
    return "job_" + "".join(reversed(digits))


def _bar(bar):
    if bar is None:
        return None
    return {
        "x": round(bar.x, 2),
        "y": round(bar.y, 2),
        "radiusPx": round(bar.radius_px, 2),
        "confidence": round(bar.confidence, 3),
    }


def _pose(pose):
    if pose is None:
        return None
    return {
        name: {"x": round(p["x"], 2), "y": round(p["y"], 2), "confidence": round(p["confidence"], 3)}
        for name, p in pose.items()
    }


def build_document(*, job_id, pose_model, video, frames, bar_detector=BAR_DETECTOR):
    """`frames` are (index, seconds, BarDetection or None, named landmarks or None), in time order."""
    return {
        "schemaVersion": SCHEMA_VERSION,
        "jobId": job_id,
        "worker": {"version": __version__, "poseModel": pose_model, "barDetector": bar_detector},
        "video": {
            "width": video["width"],
            "height": video["height"],
            "frameRate": round(video["frameRate"], 3),
            "durationS": round(video["durationS"], 3),
            "frameCount": video["frameCount"],
        },
        "frames": [
            {"index": index, "t": round(t, 4), "bar": _bar(bar), "pose": _pose(pose)}
            for index, t, bar, pose in frames
        ],
    }
