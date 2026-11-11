"""Runs the worker on the private evaluation clips and checks every output against the contract.

The clips hold people's images and live outside the repository, so this module
only runs when it is told where they are:

    VISION_EVAL_CLIPS   folder with the clips (any of .mp4 .mov .m4v)
    VISION_EVAL_MODELS  comma-separated models to run, default "full"
    VISION_EVAL_OUT     optional folder that keeps the outputs, as <model>/<clip>.json
    VISION_EVAL_FINDER  optional: the worker's --finder, "proposals" or "shoulders" (default: the worker's own)
"""

import itertools
import json
import os
from pathlib import Path

import pytest

from vision_worker import cli, models

CLIPS_DIR = os.environ.get("VISION_EVAL_CLIPS")
VARIANTS = [name.strip() for name in os.environ.get("VISION_EVAL_MODELS", "full").split(",") if name.strip()]
OUT_DIR = os.environ.get("VISION_EVAL_OUT")
FINDER = os.environ.get("VISION_EVAL_FINDER")
CLIPS = (
    sorted(p for p in Path(CLIPS_DIR).iterdir() if p.suffix.lower() in {".mp4", ".mov", ".m4v"}) if CLIPS_DIR else []
)

pytestmark = pytest.mark.skipif(not CLIPS_DIR, reason="set VISION_EVAL_CLIPS to the evaluation clips folder")


def test_the_clips_folder_has_clips():
    assert CLIPS, f"no clips found in {CLIPS_DIR}"


@pytest.mark.parametrize("variant", VARIANTS)
@pytest.mark.parametrize("clip", CLIPS, ids=lambda p: p.name)
def test_output_validates_against_the_contract(clip, variant, tmp_path, raw_output_validator):
    assert models.is_available(variant), f"{models.MODEL_FILES[variant]} is not in the model folder"
    out = (Path(OUT_DIR) if OUT_DIR else tmp_path) / variant / f"{clip.stem}.json"

    finder = ["--finder", FINDER] if FINDER else []

    code = cli.main([str(clip), "--out", str(out), "--model", variant, *finder])

    assert code == 0
    result = json.loads(out.read_text())
    errors = [f"{'/'.join(map(str, e.absolute_path))}: {e.message}" for e in raw_output_validator.iter_errors(result)]
    assert errors == []
    assert result["video"]["frameCount"] == len(result["frames"]) > 0
    times = [frame["t"] for frame in result["frames"]]
    assert all(later >= earlier for earlier, later in itertools.pairwise(times))
