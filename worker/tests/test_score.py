"""Bar scores, tested on made-up raw outputs and labels: no clip, no frame and no model is used."""

import json

import pytest

from vision_eval import score
from vision_eval.labels import PlateLabel, write_labels
from vision_eval.score import Score, ScoreError, score_output, total

WIDTH, HEIGHT = 1080, 1920
CENTRE, RADIUS = (500.0, 800.0), 100.0
COLUMNS = ["Clip", "Frames with a bar", "Labelled frames with a bar", "Centre right", "Radius right", "Both right"]


def bar(x=CENTRE[0], y=CENTRE[1], radius=RADIUS):
    return {"x": x, "y": y, "radiusPx": radius, "confidence": 0.8}


def output(bars, *, width=WIDTH, height=HEIGHT, detector="made-up-plates@1", pose_model="made-up-pose@1"):
    """A raw output with one frame for each entry of `bars` (a bar or None), 30 frames a second."""
    return {
        "schemaVersion": "1.0",
        "jobId": "job_01JC8YX4D2F7H1K9M3P5Q8S0TV",
        "worker": {"version": "0.0.1", "poseModel": pose_model, "barDetector": detector},
        "video": {
            "width": width,
            "height": height,
            "frameRate": 30.0,
            "durationS": round(len(bars) / 30, 3),
            "frameCount": len(bars),
        },
        "frames": [{"index": i, "t": round(i / 30, 4), "bar": found, "pose": None} for i, found in enumerate(bars)],
    }


def seen(frame, centre=CENTRE, radius=RADIUS, file="squat_01.mp4"):
    """A label on `frame`: the plate's centre and a rim click straight above it."""
    return PlateLabel(
        file=file,
        frame=frame,
        t=round(frame / 30, 4),
        video_width=WIDTH,
        video_height=HEIGHT,
        centre=centre,
        rim=(centre[0], centre[1] - radius),
    )


def hidden(frame, file="squat_01.mp4"):
    return PlateLabel(file=file, frame=frame, t=round(frame / 30, 4), video_width=WIDTH, video_height=HEIGHT)


def label_counts(result):
    return (result.labelled, result.found, result.centre_right, result.radius_right, result.right)


def test_the_made_up_outputs_follow_the_contract(raw_output_validator):
    assert list(raw_output_validator.iter_errors(output([bar(), None]))) == []


# --- one output against its labels ---------------------------------------------


def test_counts_the_frames_that_have_a_bar():
    result = score_output(output([bar(), None, bar(), bar()]), [])

    assert (result.frames, result.with_bar) == (4, 3)


def test_an_output_without_frames_scores_nothing():
    assert score_output(output([]), []) == Score()


def test_a_bar_on_the_labelled_plate_is_right():
    result = score_output(output([None, None, bar(503.0, 796.0, 104.0)]), [seen(2)])

    assert label_counts(result) == (1, 1, 1, 1, 1)


def test_a_labelled_frame_without_a_bar_is_not_found():
    result = score_output(output([bar(), None]), [seen(1)])

    assert label_counts(result) == (1, 0, 0, 0, 0)


@pytest.mark.parametrize(
    ("x", "y", "right"),
    [
        (525.0, 800.0, True),  # a quarter of the radius to the side
        (526.0, 800.0, False),
        (515.0, 820.0, True),  # the same distance on a slant: 15 across, 20 down
        (520.0, 820.0, False),  # under a quarter each way, but further than that in all
    ],
)
def test_the_centre_may_be_a_quarter_of_the_labelled_radius_off(x, y, right):
    result = score_output(output([bar(x, y, RADIUS)]), [seen(0)])

    assert result.centre_right == int(right)
    assert result.right == int(right)


@pytest.mark.parametrize(("radius", "right"), [(110.0, True), (90.0, True), (111.0, False), (89.0, False)])
def test_the_radius_may_be_a_tenth_of_the_labelled_radius_off(radius, right):
    result = score_output(output([bar(radius=radius)]), [seen(0)])

    assert result.radius_right == int(right)
    assert result.right == int(right)


def test_centre_and_radius_are_judged_separately():
    off_centre = score_output(output([bar(540.0, 800.0, 100.0)]), [seen(0)])
    wrong_size = score_output(output([bar(500.0, 800.0, 150.0)]), [seen(0)])

    assert label_counts(off_centre) == (1, 1, 0, 1, 0)
    assert label_counts(wrong_size) == (1, 1, 1, 0, 0)


def test_the_tolerances_follow_the_labelled_radius_not_the_found_one():
    # 40 pixels off: inside a quarter of the found radius (200), outside a quarter of the labelled one (100).
    result = score_output(output([bar(540.0, 800.0, 200.0)]), [seen(0)])

    assert result.centre_right == 0


def test_the_tolerances_can_be_changed():
    document, labels = output([bar(540.0, 800.0, 120.0)]), [seen(0)]

    assert score_output(document, labels).right == 0
    assert score_output(document, labels, centre_tolerance=0.4, radius_tolerance=0.2).right == 1


def test_a_hidden_plate_is_counted_apart():
    result = score_output(output([bar(), None, bar()]), [seen(0), hidden(1), hidden(2)])

    assert label_counts(result) == (1, 1, 1, 1, 1)
    assert (result.hidden, result.hidden_with_bar) == (2, 1)


def test_a_label_is_matched_by_frame_number_not_by_place_in_the_list():
    document = output([None, None, None, None, bar()])
    document["frames"] = document["frames"][::2]  # frames 0, 2 and 4

    assert score_output(document, [seen(4)]).right == 1


def test_a_label_for_a_frame_the_output_lacks_is_an_error():
    with pytest.raises(ScoreError, match="frame 9"):
        score_output(output([bar()]), [seen(9)])


def test_a_label_from_a_video_of_another_size_is_an_error():
    with pytest.raises(ScoreError, match="1080x1920"):
        score_output(output([bar()], width=2160, height=3840), [seen(0)])


def test_a_label_whose_time_is_not_the_frames_time_is_an_error():
    document = output([bar(), bar()])
    document["frames"][1]["t"] = 0.5

    with pytest.raises(ScoreError, match="frame 1"):
        score_output(document, [seen(1)])


def test_total_adds_the_counts():
    first = Score(frames=10, with_bar=8, labelled=2, found=2, centre_right=2, radius_right=1, right=1)
    second = Score(frames=5, labelled=1, hidden=1, hidden_with_bar=1)

    assert total([first, second]) == Score(
        frames=15, with_bar=8, labelled=3, found=2, centre_right=2, radius_right=1, right=1, hidden=1, hidden_with_bar=1
    )
    assert total([]) == Score()


# --- the command: a folder of outputs, as <model>/<clip>.json -------------------


def store(outputs, model, clip, document):
    path = outputs / model / f"{clip}.json"
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(document))
    return path


@pytest.fixture
def stored(tmp_path):
    """Made-up outputs of two models for two clips, and their labels: (outputs folder, label file)."""
    outputs, labels = tmp_path / "raw-output", tmp_path / "clips" / "plate-labels.csv"
    write_labels(labels, [seen(2), seen(7), seen(1, file="squat_02.mp4"), hidden(3, file="squat_02.mp4")])
    full, heavy = "made-up-pose-full@1", "made-up-pose-heavy@1"
    # Full: a bar on frames 0 to 7 of the first clip, too big on frame 7; none on the second clip.
    store(outputs, "full", "squat_01", output([bar()] * 7 + [bar(radius=130.0), None, None], pose_model=full))
    store(outputs, "full", "squat_02", output([None] * 5, pose_model=full))
    # Heavy: right on every frame of the first clip; on the second, a bar only where the plate is hidden.
    store(outputs, "heavy", "squat_01", output([bar()] * 10, pose_model=heavy))
    store(outputs, "heavy", "squat_02", output([None, None, None, bar(), None], pose_model=heavy))
    return outputs, labels


def run(outputs, labels, *options):
    return score.main([str(outputs), str(labels), *map(str, options)])


def section(text, model):
    """The part of the printed text under the heading of `model`."""
    return text.split(f"## {model}\n", 1)[1].split("\n## ", 1)[0]


def table(text, model):
    """The table of `model` as (column names, {clip: its other cells})."""
    lines = [line for line in section(text, model).splitlines() if line.startswith("|")]
    cells = [[cell.strip() for cell in line.strip("|").split("|")] for line in lines]
    return cells[0], {row[0]: row[1:] for row in cells[2:]}


def test_prints_a_row_for_each_clip_and_a_total_for_each_model(stored, capsys):
    assert run(*stored) == 0

    out = capsys.readouterr().out
    assert out.index("## full") < out.index("## heavy")
    assert table(out, "full") == (
        COLUMNS,
        {
            "squat_01": ["8/10 (80%)", "2/2 (100%)", "2/2 (100%)", "1/2 (50%)", "1/2 (50%)"],
            "squat_02": ["0/5 (0%)", "0/1 (0%)", "0/1 (0%)", "0/1 (0%)", "0/1 (0%)"],
            "Total, 2 clips": ["8/15 (53%)", "2/3 (67%)", "2/3 (67%)", "1/3 (33%)", "1/3 (33%)"],
        },
    )
    assert table(out, "heavy") == (
        COLUMNS,
        {
            "squat_01": ["10/10 (100%)", "2/2 (100%)", "2/2 (100%)", "2/2 (100%)", "2/2 (100%)"],
            "squat_02": ["1/5 (20%)", "0/1 (0%)", "0/1 (0%)", "0/1 (0%)", "0/1 (0%)"],
            "Total, 2 clips": ["11/15 (73%)", "2/3 (67%)", "2/3 (67%)", "2/3 (67%)", "2/3 (67%)"],
        },
    )


def test_says_what_was_scored_and_what_counts_as_right(stored, capsys):
    outputs, labels = stored

    assert run(outputs, labels) == 0

    out = capsys.readouterr().out
    assert str(outputs) in out
    assert str(labels) in out
    assert "within 0.25 of the labelled radius" in out
    assert "within 10% of the labelled radius" in out
    assert "made-up-plates@1" in section(out, "full")
    assert "made-up-pose-full@1" in section(out, "full")
    assert "made-up-pose-heavy@1" in section(out, "heavy")


def test_frames_with_a_hidden_plate_are_reported_under_the_table(stored, capsys):
    assert run(*stored) == 0

    out = capsys.readouterr().out
    assert "Labelled frames with the plate hidden: 1. A bar was reported on: 0." in section(out, "full")
    assert "Labelled frames with the plate hidden: 1. A bar was reported on: 1." in section(out, "heavy")


def test_save_writes_what_was_printed(stored, tmp_path, capsys):
    saved = tmp_path / "kept" / "bar-scores.md"

    assert run(*stored, "--save", saved) == 0

    out = capsys.readouterr().out
    assert "## full" in out
    assert saved.read_text() == out


def test_the_radius_tolerance_is_an_option(stored, capsys):
    assert run(*stored, "--radius-tolerance", 0.3) == 0

    out = capsys.readouterr().out
    assert "within 30% of the labelled radius" in out
    assert table(out, "full")[1]["squat_01"][3:] == ["2/2 (100%)", "2/2 (100%)"]


def test_the_centre_tolerance_is_an_option(stored, capsys):
    outputs, labels = stored
    store(outputs, "full", "squat_01", output([bar(x=540.0)] * 10))

    assert run(outputs, labels) == 0
    assert table(capsys.readouterr().out, "full")[1]["squat_01"][2] == "0/2 (0%)"

    assert run(outputs, labels, "--centre-tolerance", 0.5) == 0
    out = capsys.readouterr().out
    assert "within 0.5 of the labelled radius" in out
    assert table(out, "full")[1]["squat_01"][2] == "2/2 (100%)"


def test_an_output_without_labels_still_counts_its_frames(stored, capsys):
    outputs, labels = stored
    store(outputs, "full", "warm_up", output([bar(), None]))

    assert run(outputs, labels) == 0

    rows = table(capsys.readouterr().out, "full")[1]
    assert rows["warm_up"] == ["1/2 (50%)", "-", "-", "-", "-"]
    assert rows["Total, 3 clips"] == ["9/17 (53%)", "2/3 (67%)", "2/3 (67%)", "1/3 (33%)", "1/3 (33%)"]


def test_a_labelled_clip_without_an_output_is_named(stored, capsys):
    outputs, labels = stored
    (outputs / "heavy" / "squat_02.json").unlink()

    assert run(outputs, labels) == 0

    out = capsys.readouterr().out
    assert "No output for labelled clips: squat_02." in section(out, "heavy")
    assert "No output for labelled clips" not in section(out, "full")
    assert "Total, 1 clip" in table(out, "heavy")[1]


def test_a_folder_without_outputs_is_an_error(stored, tmp_path, capsys):
    empty = tmp_path / "nothing-here"
    empty.mkdir()

    assert run(empty, stored[1]) == 1
    assert "no raw outputs" in capsys.readouterr().err
    assert run(tmp_path / "no-such-folder", stored[1]) == 1
    assert "no raw outputs" in capsys.readouterr().err


def test_a_missing_label_file_is_an_error(stored, tmp_path, capsys):
    assert run(stored[0], tmp_path / "no-labels.csv") == 1

    captured = capsys.readouterr()
    assert "no labels" in captured.err
    assert captured.out == ""


def test_an_output_that_does_not_fit_its_labels_is_an_error_naming_the_file(stored, tmp_path, capsys):
    outputs, labels = stored
    path = store(outputs, "heavy", "squat_01", output([bar()] * 10, width=720, height=1280))
    saved = tmp_path / "bar-scores.md"

    assert run(outputs, labels, "--save", saved) == 1

    captured = capsys.readouterr()
    assert str(path) in captured.err
    assert captured.out == ""
    assert not saved.exists()


@pytest.mark.parametrize(
    "text", ["not JSON", '{"schemaVersion": "2.0"}', "{}", "[]"], ids=["not-json", "other-version", "empty", "a-list"]
)
def test_a_file_that_is_not_a_raw_output_is_an_error_naming_the_file(text, stored, capsys):
    outputs, labels = stored
    path = outputs / "full" / "squat_01.json"
    path.write_text(text)

    assert run(outputs, labels) == 1

    captured = capsys.readouterr()
    assert str(path) in captured.err
    assert captured.out == ""
