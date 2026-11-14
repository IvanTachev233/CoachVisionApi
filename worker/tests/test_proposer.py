"""The detector model as a proposer of plate boxes. The model itself is faked except in the last test."""

import json

import numpy as np
import pytest
from conftest import FakePose, fake_pose, squat_scenes, write_clip

from vision_worker import cli, models
from vision_worker.bar import BAR_DETECTOR, MARKED_DETECTOR
from vision_worker.proposer import DFineProposer

WIDTH, HEIGHT = 720, 1280
RADIUS = 96.0
JOB_ID = "job_01JC8YX4D2F7H1K9M3P5Q8S0TV"


class FakeSession:
    """Stands in for the ONNX Runtime session: records what it was fed and replays an answer."""

    def __init__(self, labels, boxes, scores):
        self._answer = [np.array([labels]), np.array([boxes], np.float32), np.array([scores], np.float32)]
        self.fed = None

    def run(self, output_names, input_feed):
        self.names = output_names
        self.fed = input_feed
        return self._answer


def test_the_picture_goes_in_as_one_square_rgb_image_between_0_and_1_with_its_real_size():
    session = FakeSession([], np.zeros((0, 4)), [])
    image = np.zeros((360, 200, 3), np.uint8)
    image[..., 2] = 255  # pure red, in OpenCV's blue-green-red order

    DFineProposer(session=session).propose(image)

    pixels = session.fed["images"]
    assert pixels.shape == (1, 3, 640, 640) and pixels.dtype == np.float32
    assert pixels[0, 0].min() == 1.0 and pixels[0, 1].max() == 0.0 and pixels[0, 2].max() == 0.0
    assert session.fed["orig_target_sizes"].tolist() == [[200, 360]]
    assert session.fed["orig_target_sizes"].dtype == np.int64
    assert session.names == ["labels", "boxes", "scores"]


def test_a_box_named_twice_comes_out_once_with_its_best_score_and_boxes_come_best_first():
    session = FakeSession(
        labels=[5, 9, 2],
        boxes=[[10, 20, 50, 60], [10, 20, 50, 60], [100, 100, 180, 190]],
        scores=[0.2, 0.45, 0.3],
    )

    boxes = DFineProposer(session=session).propose(np.zeros((360, 200, 3), np.uint8))

    assert boxes == [(10.0, 20.0, 50.0, 60.0, pytest.approx(0.45)), (100.0, 100.0, 180.0, 190.0, pytest.approx(0.3))]


def test_the_proposer_has_a_name_with_a_version():
    assert DFineProposer.name.rsplit("@", 1)[1].isdigit()


# --- the model file ---------------------------------------------------------------


def test_a_missing_proposer_file_is_an_error_naming_it(tmp_path):
    assert not models.proposer_is_available(tmp_path)
    with pytest.raises(models.ModelError, match=models.PROPOSER_FILE):
        models.proposer_path(tmp_path)


def test_a_proposer_file_that_is_not_the_pinned_one_is_refused(tmp_path):
    (tmp_path / models.PROPOSER_FILE).write_bytes(b"something else")

    assert models.proposer_is_available(tmp_path)
    with pytest.raises(models.ModelError, match="pinned"):
        models.proposer_path(tmp_path)


# --- the command ------------------------------------------------------------------


class BoxesAtThePlate:
    name = "fake-proposer@1"

    def __init__(self, centres, frame_count):
        self._centres = [centres[frame_count * (k + 1) // 4] for k in range(3)]
        self._calls = 0

    def propose(self, image):
        (cx, cy), scale = self._centres[self._calls], image.shape[0] / HEIGHT
        self._calls += 1
        r = RADIUS * scale
        return [(cx * scale - r, cy * scale - r, cx * scale + r, cy * scale + r, 0.4)]

    def close(self):
        self.closed = True


@pytest.fixture
def faked(tmp_path, monkeypatch):
    """A clip, a pose model and a proposer that are all faked: (clip, plate centres, the proposers made)."""
    frames, centres = squat_scenes(WIDTH, HEIGHT, 60, RADIUS)
    clip = write_clip(tmp_path / "clip.mp4", frames)
    folder = tmp_path / "models"
    folder.mkdir()
    for name in models.MODEL_FILES.values():
        (folder / name).write_bytes(b"placeholder")
    poses = [fake_pose(centre, i) for i, centre in enumerate(centres)]
    made = []
    monkeypatch.setattr(cli, "PoseLandmarkerEstimator", lambda model_file: FakePose(poses))
    monkeypatch.setattr(models, "verify", lambda variant, path: None)
    monkeypatch.setattr(models, "proposer_path", lambda folder=None: "proposer.onnx")
    monkeypatch.setattr(cli, "DFineProposer", lambda model_file: made.append(BoxesAtThePlate(centres, 60)) or made[-1])
    return clip, centres, made, folder


def test_finder_proposals_uses_the_proposer_and_names_it_in_the_output(faked, tmp_path, raw_output_validator):
    clip, centres, made, folder = faked
    out = tmp_path / "result.json"

    code = cli.main(
        [str(clip), "--out", str(out), "--model-dir", str(folder), "--job-id", JOB_ID, "--finder", "proposals"]
    )

    assert code == 0
    result = json.loads(out.read_text())
    assert list(raw_output_validator.iter_errors(result)) == []
    assert result["worker"]["barDetector"] == "fake-proposer@1+opencv-follow@1"
    bars = [(frame["bar"], centre) for frame, centre in zip(result["frames"], centres) if frame["bar"]]
    assert len(bars) >= 0.8 * 60
    assert all(abs(bar["x"] - x) <= 14 and abs(bar["y"] - y) <= 14 for bar, (x, y) in bars)
    assert made[0].closed


def test_without_the_finder_option_the_proposer_is_used(faked, tmp_path):
    clip, _, made, folder = faked
    out = tmp_path / "result.json"

    assert cli.main([str(clip), "--out", str(out), "--model-dir", str(folder)]) == 0
    assert json.loads(out.read_text())["worker"]["barDetector"] == "fake-proposer@1+opencv-follow@1"
    assert len(made) == 1 and made[0].closed


def test_finder_shoulders_makes_no_proposer_and_names_the_circle_finder(faked, tmp_path):
    clip, _, made, folder = faked
    out = tmp_path / "result.json"

    assert cli.main([str(clip), "--out", str(out), "--model-dir", str(folder), "--finder", "shoulders"]) == 0
    assert json.loads(out.read_text())["worker"]["barDetector"] == BAR_DETECTOR
    assert made == []


@pytest.mark.parametrize("finder", [[], ["--finder", "proposals"]], ids=["default", "proposals"])
def test_finding_by_proposals_without_the_model_file_fails_with_its_name(finder, faked, tmp_path, monkeypatch, capsys):
    clip, _, _, folder = faked
    monkeypatch.undo()
    monkeypatch.setattr(cli, "PoseLandmarkerEstimator", lambda model_file: FakePose([]))
    monkeypatch.setattr(models, "verify", lambda variant, path: None)
    out = tmp_path / "result.json"

    assert cli.main([str(clip), "--out", str(out), "--model-dir", str(folder), *finder]) == 1
    assert models.PROPOSER_FILE in capsys.readouterr().err
    assert not out.exists()


@pytest.mark.parametrize("finder", ["proposals", "shoulders"])
def test_a_mark_and_a_finder_cannot_be_given_together(finder, faked, tmp_path):
    clip, _, _, folder = faked

    with pytest.raises(SystemExit) as exit_info:
        cli.main([str(clip), "--model-dir", str(folder), "--finder", finder, "--mark", "0.5", "1", "1", "1", "9"])

    assert exit_info.value.code == 2


def test_a_mark_needs_no_proposer_and_no_proposer_file(faked, tmp_path, monkeypatch):
    clip, centres, made, folder = faked
    monkeypatch.undo()
    monkeypatch.setattr(cli, "PoseLandmarkerEstimator", lambda model_file: FakePose([None] * 60))
    monkeypatch.setattr(models, "verify", lambda variant, path: None)
    out = tmp_path / "result.json"
    x, y = centres[15]
    mark = ["--mark", "0.5", str(x), str(y), str(x), str(y - RADIUS)]

    assert cli.main([str(clip), "--out", str(out), "--model-dir", str(folder), *mark]) == 0
    assert json.loads(out.read_text())["worker"]["barDetector"].startswith(MARKED_DETECTOR)
    assert made == []


@pytest.mark.skipif(not models.proposer_is_available(), reason=f"{models.PROPOSER_FILE} is not in the model folder")
def test_the_real_model_answers_with_boxes_inside_the_picture():
    frames, _ = squat_scenes(360, 640, 1, 48.0)
    proposer = DFineProposer(models.proposer_path())

    boxes = proposer.propose(frames[0])
    proposer.close()

    assert len(boxes) > 0
    scores = [score for *_, score in boxes]
    assert scores == sorted(scores, reverse=True) and all(0.0 <= score <= 1.0 for score in scores)
    assert all(-40 <= left < right <= 400 and -40 <= top < bottom <= 680 for left, top, right, bottom, _ in boxes)
