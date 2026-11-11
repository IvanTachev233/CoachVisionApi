import json

import numpy as np
import pytest
from conftest import FakePose, fake_pose, squat_scenes, write_clip

from vision_worker import cli, models

JOB_ID = "job_01JC8YX4D2F7H1K9M3P5Q8S0TV"
WIDTH, HEIGHT, RADIUS, FRAMES = 720, 1280, 96.0, 45


@pytest.fixture
def model_dir(tmp_path):
    """A model folder holding placeholder files, for runs where the pose model itself is faked."""
    folder = tmp_path / "models"
    folder.mkdir()
    for name in models.MODEL_FILES.values():
        (folder / name).write_bytes(b"placeholder")
    return folder


class NoBoxes:
    """Stands in for the plate proposer: it never sees a plate."""

    name = "no-boxes@1"

    def propose(self, image):
        return []

    def close(self):
        pass


def fake_landmarker(monkeypatch, poses):
    """Replace the Pose Landmarker with one that replays `poses`, and skip the checksum of the placeholder.

    The plate proposer, which the worker uses unless told otherwise, is replaced by one without boxes.
    """
    created = []
    monkeypatch.setattr(models, "proposer_path", lambda folder=None: "proposer.onnx")
    monkeypatch.setattr(cli, "DFineProposer", lambda model_file: NoBoxes())

    def build(model_file):
        created.append(FakePose(poses))
        return created[-1]

    monkeypatch.setattr(cli, "PoseLandmarkerEstimator", build)
    monkeypatch.setattr(models, "verify", lambda variant, path: None)
    return created


def synthetic_clip(tmp_path, *, plate):
    frames, centres = squat_scenes(WIDTH, HEIGHT, FRAMES, RADIUS, plate=plate)
    clip = write_clip(tmp_path / "clip.mp4", frames)
    return clip, [fake_pose(centre, i) for i, centre in enumerate(centres)], centres


def test_writes_valid_raw_output_for_a_clip(tmp_path, model_dir, monkeypatch, raw_output_validator):
    clip, poses, centres = synthetic_clip(tmp_path, plate=True)
    created = fake_landmarker(monkeypatch, poses)
    out = tmp_path / "out" / "result.json"
    options = ["--model-dir", str(model_dir), "--job-id", JOB_ID, "--finder", "shoulders"]

    code = cli.main([str(clip), "--out", str(out), *options])

    assert code == 0
    result = json.loads(out.read_text())
    assert list(raw_output_validator.iter_errors(result)) == []
    assert result["jobId"] == JOB_ID
    assert result["worker"]["poseModel"].startswith("mediapipe-pose-landmarker-full@")
    assert result["video"]["width"] == WIDTH
    assert result["video"]["height"] == HEIGHT
    assert result["video"]["frameCount"] == len(result["frames"]) == FRAMES
    assert [frame["index"] for frame in result["frames"]] == list(range(FRAMES))
    assert all(frame["pose"] is not None for frame in result["frames"])
    bars = [(frame["bar"], centre) for frame, centre in zip(result["frames"], centres) if frame["bar"]]
    assert len(bars) >= 0.8 * FRAMES
    assert all(abs(bar["x"] - cx) <= 14 and abs(bar["y"] - cy) <= 14 for bar, (cx, cy) in bars)
    assert created[0].closed


def test_clip_with_no_plates_gives_valid_output_without_bar_positions(
    tmp_path, model_dir, monkeypatch, raw_output_validator
):
    clip, poses, _ = synthetic_clip(tmp_path, plate=False)
    fake_landmarker(monkeypatch, poses)
    out = tmp_path / "result.json"

    code = cli.main([str(clip), "--out", str(out), "--model-dir", str(model_dir)])

    assert code == 0
    result = json.loads(out.read_text())
    assert list(raw_output_validator.iter_errors(result)) == []
    assert len(result["frames"]) == FRAMES
    assert all(frame["bar"] is None for frame in result["frames"])


def test_model_option_picks_the_heavy_model(tmp_path, model_dir, monkeypatch):
    clip, poses, _ = synthetic_clip(tmp_path, plate=False)
    fake_landmarker(monkeypatch, poses)
    out = tmp_path / "result.json"

    code = cli.main([str(clip), "--out", str(out), "--model-dir", str(model_dir), "--model", "heavy"])

    assert code == 0
    assert json.loads(out.read_text())["worker"]["poseModel"].startswith("mediapipe-pose-landmarker-heavy@")


def test_prints_to_standard_output_without_an_out_path(tmp_path, model_dir, monkeypatch, capsys, raw_output_validator):
    clip, poses, _ = synthetic_clip(tmp_path, plate=False)
    fake_landmarker(monkeypatch, poses)

    code = cli.main([str(clip), "--model-dir", str(model_dir)])

    assert code == 0
    result = json.loads(capsys.readouterr().out)
    assert list(raw_output_validator.iter_errors(result)) == []
    assert result["jobId"].startswith("job_")


def test_missing_video_fails_with_a_message_and_writes_nothing(tmp_path, model_dir, monkeypatch, capsys):
    fake_landmarker(monkeypatch, [])
    out = tmp_path / "result.json"

    code = cli.main([str(tmp_path / "absent.mp4"), "--out", str(out), "--model-dir", str(model_dir)])

    assert code == 1
    assert "absent.mp4" in capsys.readouterr().err
    assert not out.exists()


def test_missing_model_file_fails_with_its_name(tmp_path, capsys):
    clip, _, _ = synthetic_clip(tmp_path, plate=False)
    empty = tmp_path / "no-models"
    empty.mkdir()

    code = cli.main([str(clip), "--model-dir", str(empty), "--model", "heavy"])

    assert code == 1
    assert "pose_landmarker_heavy.task" in capsys.readouterr().err


def test_job_id_must_match_the_contract(tmp_path, model_dir):
    with pytest.raises(SystemExit) as exit_info:
        cli.main([str(tmp_path / "clip.mp4"), "--model-dir", str(model_dir), "--job-id", "job_123"])

    assert exit_info.value.code == 2


@pytest.mark.skipif(
    not (models.is_available("full") and models.proposer_is_available()),
    reason=f"pose_landmarker_full.task or {models.PROPOSER_FILE} is not in the model folder",
)
def test_real_model_on_a_clip_with_no_person_and_no_plate(tmp_path, raw_output_validator):
    frames, _ = squat_scenes(360, 640, 20, 48.0, plate=False)
    clip = write_clip(tmp_path / "clip.mp4", frames)
    out = tmp_path / "result.json"

    code = cli.main([str(clip), "--out", str(out)])

    assert code == 0
    result = json.loads(out.read_text())
    assert list(raw_output_validator.iter_errors(result)) == []
    assert len(result["frames"]) == 20
    assert all(frame["bar"] is None for frame in result["frames"])


def stepping_poses(poses):
    """`poses` with a left ankle that wobbles about one place, as a planted foot is measured."""
    wobble = np.random.default_rng(6).uniform(-3.0, 3.0, size=(len(poses), 2))
    return [
        {**pose, "left_ankle": {"x": 200.0 + dx, "y": 1100.0 + dy, "confidence": 0.9}}
        for pose, (dx, dy) in zip(poses, wobble)
    ]


@pytest.mark.parametrize(("lift", "held"), [("squat", True), ("other", False), (None, False)])
def test_the_lift_says_whether_the_feet_are_held_in_place(tmp_path, model_dir, monkeypatch, lift, held):
    clip, poses, _ = synthetic_clip(tmp_path, plate=True)
    fake_landmarker(monkeypatch, stepping_poses(poses))
    out = tmp_path / "result.json"
    options = ["--model-dir", str(model_dir), "--finder", "shoulders", *(["--lift", lift] if lift else [])]

    assert cli.main([str(clip), "--out", str(out), *options]) == 0

    ankles = {(f["pose"]["left_ankle"]["x"], f["pose"]["left_ankle"]["y"]) for f in json.loads(out.read_text())["frames"]}
    assert (len(ankles) == 1) == held


def test_a_lift_the_api_does_not_know_is_refused(tmp_path, model_dir):
    with pytest.raises(SystemExit):
        cli.main([str(tmp_path / "clip.mp4"), "--model-dir", str(model_dir), "--lift", "juggling"])
