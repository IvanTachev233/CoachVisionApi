"""Trying each label as the mark, on synthetic clips: no real clip and no pose model is used."""

import json

import pytest
from conftest import squat_scenes, write_clip

from vision_eval import marked
from vision_eval.labels import PlateLabel, write_labels
from vision_worker.bar import MARKED_DETECTOR

WIDTH, HEIGHT = 360, 640
RADIUS = 48.0
FRAMES = 40
LABELLED = (5, 15, 25, 35)


def label(file, frame, centre=None):
    if centre is None:
        return PlateLabel(file=file, frame=frame, t=round(frame / 30, 4), video_width=WIDTH, video_height=HEIGHT)
    return PlateLabel(
        file=file,
        frame=frame,
        t=round(frame / 30, 4),
        video_width=WIDTH,
        video_height=HEIGHT,
        centre=centre,
        rim=(centre[0], centre[1] - RADIUS),
    )


@pytest.fixture
def clips(tmp_path):
    """A folder with one synthetic squat clip and its labels: three on the plate, one hidden."""
    folder = tmp_path / "clips"
    folder.mkdir()
    frames, centres = squat_scenes(WIDTH, HEIGHT, FRAMES, RADIUS)
    write_clip(folder / "squat_01.mp4", frames)
    labels = [label("squat_01.mp4", frame, centres[frame]) for frame in LABELLED[:3]]
    labels.append(label("squat_01.mp4", LABELLED[3]))
    write_labels(folder / "plate-labels.csv", labels)
    return folder


def cells(text, clip):
    line = next(line for line in text.splitlines() if line.startswith(f"| {clip}"))
    return [cell.strip() for cell in line.strip("|").split("|")][1:]


def test_each_visible_label_is_the_mark_once_and_the_others_score_it(clips, tmp_path, capsys, raw_output_validator):
    out = tmp_path / "marked-outputs"

    assert marked.main([str(clips), str(out), "--no-colour", "--no-partial-rim"]) == 0

    files = sorted(path.name for path in (out / "marked").iterdir())
    assert files == [f"squat_01.mark-{frame:05d}.json" for frame in LABELLED[:3]]
    for name in files:
        document = json.loads((out / "marked" / name).read_text())
        assert list(raw_output_validator.iter_errors(document)) == []
        assert document["worker"]["barDetector"] == MARKED_DETECTOR
        assert len(document["frames"]) == FRAMES
    text = capsys.readouterr().out
    # Three marks; each is scored on the two other visible labels, never on its own.
    assert cells(text, "squat_01")[1:] == ["6/6 (100%)"] * 4
    frames_with_a_bar = int(cells(text, "squat_01")[0].split("/")[0])
    assert frames_with_a_bar >= 0.8 * 3 * FRAMES
    assert cells(text, "squat_01")[0].split()[0].endswith(f"/{3 * FRAMES}")
    assert "Marks tried: 3. No plate measured at the mark: 0." in text
    assert (out / "bar-scores.md").read_text() == text


def test_a_mark_that_is_not_on_a_plate_counts_as_tried_and_scores_nothing(clips, tmp_path, capsys):
    _, centres = squat_scenes(WIDTH, HEIGHT, FRAMES, RADIUS)
    labels = [label("squat_01.mp4", 5, (WIDTH * 0.2, HEIGHT * 0.85)), label("squat_01.mp4", 15, centres[15])]
    write_labels(clips / "plate-labels.csv", labels)

    assert marked.main([str(clips), str(tmp_path / "out")]) == 0

    text = capsys.readouterr().out
    assert "Marks tried: 2. No plate measured at the mark: 1." in text
    # The good mark is scored on the bad label (wrong), the bad mark on the good label (no bar).
    assert cells(text, "squat_01")[1] == "1/2 (50%)"
    assert cells(text, "squat_01")[4] == "0/2 (0%)"


def test_a_folder_without_labels_is_an_error(tmp_path, capsys):
    assert marked.main([str(tmp_path), str(tmp_path / "out")]) == 1
    assert "no labels" in capsys.readouterr().err


def test_a_labelled_clip_that_is_missing_is_an_error(clips, tmp_path, capsys):
    (clips / "squat_01.mp4").unlink()

    assert marked.main([str(clips), str(tmp_path / "out")]) == 1
    assert "squat_01.mp4" in capsys.readouterr().err


def test_both_follow_options_are_on_by_default_and_name_the_runs_folder_and_the_detector(clips, tmp_path, capsys):
    out = tmp_path / "out"

    assert marked.main([str(clips), str(out)]) == 0

    runs = out / "marked+colour+partial-rim"
    assert len(list(runs.iterdir())) == 3
    document = json.loads(next(runs.iterdir()).read_text())
    assert document["worker"]["barDetector"] == MARKED_DETECTOR + "+colour+partial-rim"
    text = capsys.readouterr().out
    assert "## marked+colour+partial-rim" in text
    assert cells(text, "squat_01")[1:] == ["6/6 (100%)"] * 4


def test_one_follow_option_can_be_turned_off(clips, tmp_path):
    out = tmp_path / "out"

    assert marked.main([str(clips), str(out), "--no-colour"]) == 0

    assert [path.name for path in out.iterdir() if path.is_dir()] == ["marked+partial-rim"]
