import math

import pytest

from vision_eval.labels import (
    COLUMNS,
    LabelError,
    PlateLabel,
    labelled_files,
    pick_frames,
    read_labels,
    replace_clip,
    write_labels,
)


def seen(file="squat_01.mp4", frame=12, centre=(512.3, 804.0), rim=(512.3, 675.5)):
    return PlateLabel(file=file, frame=frame, t=0.4004, video_width=1080, video_height=1920, centre=centre, rim=rim)


def hidden(file="squat_01.mp4", frame=40):
    return PlateLabel(file=file, frame=frame, t=1.3347, video_width=1080, video_height=1920)


# --- the circle two clicks describe ------------------------------------------


def test_radius_is_the_distance_from_the_centre_to_the_rim_click():
    assert seen(centre=(100.0, 200.0), rim=(100.0, 150.0)).radius_px == pytest.approx(50.0)
    assert seen(centre=(100.0, 200.0), rim=(130.0, 240.0)).radius_px == pytest.approx(50.0)
    assert seen(centre=(0.0, 0.0), rim=(1.0, 1.0)).radius_px == pytest.approx(math.sqrt(2))


def test_a_label_without_clicks_is_a_hidden_plate():
    label = hidden()

    assert not label.visible
    assert label.radius_px is None
    assert seen().visible


def test_a_label_needs_both_clicks_or_neither():
    with pytest.raises(LabelError):
        PlateLabel(file="a.mp4", frame=0, t=0.0, video_width=10, video_height=10, centre=(1.0, 1.0))


def test_the_two_clicks_must_be_apart():
    with pytest.raises(LabelError):
        seen(centre=(5.0, 5.0), rim=(5.0, 5.0))


# --- which frames get a label --------------------------------------------------


def test_frames_are_spread_over_the_clip():
    assert pick_frames(100) == [10, 30, 50, 70, 90]
    assert pick_frames(300, wanted=3) == [50, 150, 250]


def test_a_short_clip_gives_every_frame_once():
    assert pick_frames(3) == [0, 1, 2]
    assert pick_frames(5) == [0, 1, 2, 3, 4]
    assert pick_frames(7) == sorted(set(pick_frames(7)))
    assert len(pick_frames(7)) == 5


def test_no_frames_no_picks():
    assert pick_frames(0) == []


# --- the file -------------------------------------------------------------------


def test_file_has_one_header_and_one_row_per_label(tmp_path):
    path = tmp_path / "plate-labels.csv"

    write_labels(path, [seen(), hidden()])

    lines = path.read_text().splitlines()
    assert lines[0] == ",".join(COLUMNS)
    assert lines[0] == "file,frame,t,visible,centre_x,centre_y,rim_x,rim_y,radius_px,video_width,video_height"
    assert lines[1] == "squat_01.mp4,12,0.4004,yes,512.3,804.0,512.3,675.5,128.5,1080,1920"
    assert lines[2] == "squat_01.mp4,40,1.3347,no,,,,,,1080,1920"
    assert len(lines) == 3


def test_written_labels_read_back_the_same(tmp_path):
    path = tmp_path / "plate-labels.csv"
    labels = [seen(), hidden(), seen(file="squat_02.mp4", frame=3)]

    write_labels(path, labels)

    assert read_labels(path) == labels


def test_numbers_are_rounded_in_the_file(tmp_path):
    path = tmp_path / "plate-labels.csv"
    size = {"video_width": 720, "video_height": 1280}
    rough = PlateLabel(file="a.mp4", frame=1, t=0.03336667, centre=(10.04, 20.06), rim=(10.04, 60.26), **size)

    write_labels(path, [rough])

    assert path.read_text().splitlines()[1] == "a.mp4,1,0.0334,yes,10.0,20.1,10.0,60.3,40.2,720,1280"


def test_rows_are_ordered_by_clip_then_frame(tmp_path):
    path = tmp_path / "plate-labels.csv"

    write_labels(path, [seen(file="b.mp4", frame=9), seen(file="a.mp4", frame=30), seen(file="a.mp4", frame=4)])

    assert [(label.file, label.frame) for label in read_labels(path)] == [("a.mp4", 4), ("a.mp4", 30), ("b.mp4", 9)]


def test_a_missing_file_holds_no_labels(tmp_path):
    assert read_labels(tmp_path / "plate-labels.csv") == []


def test_a_file_with_other_columns_is_refused(tmp_path):
    path = tmp_path / "plate-labels.csv"
    path.write_text("file,reps,plate_colour\nsquat_01.mp4,5,green\n")

    with pytest.raises(LabelError, match="plate-labels.csv"):
        read_labels(path)


def test_a_broken_row_is_refused_with_its_line_number(tmp_path):
    path = tmp_path / "plate-labels.csv"
    write_labels(path, [seen()])
    path.write_text(path.read_text() + "squat_01.mp4,twelve,0.5,yes,1,2,3,4,5,1080,1920\n")

    with pytest.raises(LabelError, match="line 3"):
        read_labels(path)


def test_writing_leaves_no_other_file_behind(tmp_path):
    path = tmp_path / "plate-labels.csv"

    write_labels(path, [seen()])
    write_labels(path, [seen(), hidden()])

    assert [p.name for p in tmp_path.iterdir()] == ["plate-labels.csv"]


# --- per clip -------------------------------------------------------------------


def test_replacing_a_clips_labels_keeps_the_other_clips():
    before = [seen(file="a.mp4", frame=1), seen(file="b.mp4", frame=2), seen(file="a.mp4", frame=3)]
    new = [hidden(file="a.mp4", frame=7)]

    after = replace_clip(before, "a.mp4", new)

    assert after == [hidden(file="a.mp4", frame=7), seen(file="b.mp4", frame=2)]
    assert len(before) == 3


def test_labelled_files_are_those_with_rows():
    labels = [seen(file="a.mp4"), hidden(file="a.mp4"), seen(file="c.mp4")]

    assert labelled_files(labels) == {"a.mp4", "c.mp4"}
