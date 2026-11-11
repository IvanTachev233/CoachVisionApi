"""Finds the model files (Pose Landmarker, plate proposer) and checks they are the pinned ones.

Model files are never in git. README.md says where to download them.
"""

import hashlib
import os
from pathlib import Path

MODEL_FILES = {
    "full": "pose_landmarker_full.task",
    "heavy": "pose_landmarker_heavy.task",
}

# SHA-256 of the files the evaluation was run with. A different file is refused,
# so results always name the model that produced them.
MODEL_SHA256 = {
    "full": "4eaa5eb7a98365221087693fcc286334cf0858e2eb6e15b506aa4a7ecdcec4ad",
    "heavy": "64437af838a65d18e5ba7a0d39b465540069bc8aae8308de3e318aad31fcbc7b",
}

# The plate proposer: D-FINE-S (Objects365) exported to ONNX. README.md says how the file is made.
PROPOSER_FILE = "dfine_s_obj365.onnx"
PROPOSER_SHA256 = "02a82c513691065804b8b95bb334a9740c8a2c17cf526e327060497fa21809a0"

DEFAULT_MODEL_DIR = Path(__file__).resolve().parents[1] / "models"


class ModelError(Exception):
    """The model file is missing or is not the pinned file."""


def model_dir(folder=None):
    """The folder to look in: the argument, then VISION_MODEL_DIR, then worker/models."""
    return Path(folder or os.environ.get("VISION_MODEL_DIR") or DEFAULT_MODEL_DIR)


def is_available(variant, folder=None):
    return (model_dir(folder) / MODEL_FILES[variant]).is_file()


def verify(variant, path):
    digest = hashlib.sha256(Path(path).read_bytes()).hexdigest()
    if digest != MODEL_SHA256[variant]:
        raise ModelError(f"{path} is not the pinned {variant} model (SHA-256 {digest})")


def model_path(variant, folder=None):
    """The model file for `variant`, checked against its pinned SHA-256."""
    path = model_dir(folder) / MODEL_FILES[variant]
    if not path.is_file():
        raise ModelError(f"model file not found: {path} (README.md has the download command)")
    verify(variant, path)
    return path


def proposer_is_available(folder=None):
    return (model_dir(folder) / PROPOSER_FILE).is_file()


def proposer_path(folder=None):
    """The plate proposer's model file, checked against its pinned SHA-256."""
    path = model_dir(folder) / PROPOSER_FILE
    if not path.is_file():
        raise ModelError(f"model file not found: {path} (README.md says how it is made)")
    digest = hashlib.sha256(path.read_bytes()).hexdigest()
    if digest != PROPOSER_SHA256:
        raise ModelError(f"{path} is not the pinned plate proposer (SHA-256 {digest})")
    return path
