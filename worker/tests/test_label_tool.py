"""The labelling tool, driven by scripted clicks and keys instead of a person at a window."""

import numpy as np
import pytest
from conftest import squat_scenes, write_clip

from vision_eval import label_tool
from vision_eval.label_tool import BANNER, MARK_COLOUR, FrameLabeller, View, fit
from vision_eval.labels import pick_frames, read_labels

SPACE, ENTER, ESCAPE = 32, 13, 27
NO_KEY = -1


class ScriptedWindow:
    """Stands in for the real window: each wait for a key plays the next scripted click, move or key."""

    def __init__(self, script):
        self.script = list(script)
        self.shown = []
        self.notices = []
        self.was_closed = False
        self._click = self._move = None

    def on_mouse(self, click, move):
        self._click, self._move = click, move

    def show(self, image):
        self.shown.append(image.shape)

    def notice(self, text):
        self.notices.append(text)

    def wait_key(self, milliseconds):
        if not self.script:
            raise AssertionError("the tool waited for input after the script ended")
        kind, *values = self.script.pop(0)
        if kind == "click":
            self._click(*values)
            return NO_KEY
        if kind == "move":
            self._move(*values)
            return NO_KEY
        return values[0]

    def is_closed(self):
        return False

    def close(self):
        self.was_closed = True


def labeller(width=300, height=400, scale=0.5):
    image = np.full((height, width, 3), 90, dtype=np.uint8)
    return FrameLabeller(image, View(scale=scale, top=BANNER), heading="clip.mp4  frame 1/5"), image


def on_picture(x, y):
    """Window coordinates of a point given in the shown picture's own pixels."""
    return x, y + BANNER


# --- window pixels and video pixels ----------------------------------------------


def test_view_maps_window_pixels_to_video_pixels_and_back():
    view = View(scale=0.5, top=BANNER)

    assert view.to_video(100, BANNER + 40) == (200.0, 80.0)
    assert view.to_window(200.0, 80.0) == (100, BANNER + 40)


def test_large_frames_are_shrunk_to_fit_and_small_ones_left_alone():
    big = np.zeros((1920, 1080, 3), dtype=np.uint8)
    small = np.zeros((400, 300, 3), dtype=np.uint8)

    shown, scale = fit(big, max_width=1600, max_height=960)
    assert shown.shape == (960, 540, 3)
    assert scale == pytest.approx(0.5)

    shown, scale = fit(small, max_width=1600, max_height=960)
    assert shown.shape == (400, 300, 3)
    assert scale == 1.0


# --- one frame ------------------------------------------------------------------


def test_two_clicks_make_a_plate_in_video_pixels():
    tool, _ = labeller(scale=0.5)

    tool.click(*on_picture(100, 200))
    assert tool.result() is None
    tool.click(*on_picture(100, 150))

    assert tool.result() == ((200.0, 400.0), (200.0, 300.0))
    assert tool.key(SPACE) == "accept"
    assert tool.key(ENTER) == "accept"


def test_nothing_to_keep_before_both_clicks():
    tool, _ = labeller()

    assert tool.key(SPACE) is None
    tool.click(*on_picture(100, 200))
    assert tool.key(SPACE) is None


def test_a_click_on_the_banner_is_ignored():
    tool, _ = labeller()

    tool.click(100, BANNER - 5)

    tool.click(*on_picture(100, 200))
    tool.click(*on_picture(100, 150))
    assert tool.result() == ((200.0, 400.0), (200.0, 300.0))


def test_a_second_click_on_the_centre_is_ignored():
    tool, _ = labeller()

    tool.click(*on_picture(100, 200))
    tool.click(*on_picture(101, 201))

    assert tool.result() is None
    tool.click(*on_picture(100, 150))
    assert tool.result() is not None


def test_a_third_click_starts_again_from_that_click():
    tool, _ = labeller(scale=1.0)

    tool.click(*on_picture(100, 200))
    tool.click(*on_picture(100, 150))
    tool.click(*on_picture(40, 60))

    assert tool.result() is None
    tool.click(*on_picture(40, 90))
    assert tool.result() == ((40.0, 60.0), (40.0, 90.0))


def test_r_starts_the_frame_again():
    tool, _ = labeller()
    tool.click(*on_picture(100, 200))
    tool.click(*on_picture(100, 150))

    assert tool.key(ord("r")) is None

    assert tool.result() is None
    assert tool.key(SPACE) is None


def test_h_marks_the_plate_hidden_and_space_keeps_that():
    tool, _ = labeller()
    tool.click(*on_picture(100, 200))

    # h alone moves nothing on: a held key must not run through the frames.
    assert tool.key(ord("h")) is None
    assert tool.key(ord("h")) is None

    assert tool.result() == (None, None)
    assert tool.key(SPACE) == "accept"


def test_a_click_after_h_means_the_plate_is_there_after_all():
    tool, _ = labeller()
    tool.key(ord("h"))

    tool.click(*on_picture(100, 200))

    assert tool.result() is None
    assert tool.key(SPACE) is None
    tool.click(*on_picture(100, 150))
    assert tool.result() == ((200.0, 400.0), (200.0, 300.0))


def test_r_also_undoes_h():
    tool, _ = labeller()
    tool.key(ord("h"))

    tool.key(ord("r"))

    assert tool.result() is None
    assert tool.key(SPACE) is None


def test_keys_for_stopping_and_keys_that_do_nothing():
    tool, _ = labeller()

    assert tool.key(ord("q")) == "quit"
    assert tool.key(ESCAPE) == "quit"
    assert tool.key(ord("x")) is None
    assert tool.key(NO_KEY) is None


def test_the_picture_is_drawn_under_a_banner_and_the_frame_is_left_untouched():
    tool, image = labeller(width=300, height=400)
    before = image.copy()
    tool.click(*on_picture(100, 200))
    tool.click(*on_picture(100, 150))

    canvas = tool.render()

    assert canvas.shape == (400 + BANNER, 300, 3)
    assert np.array_equal(image, before)
    # The circle passes through the rim click and its opposite point.
    assert tuple(canvas[BANNER + 250, 100]) == MARK_COLOUR
    assert tuple(canvas[BANNER + 200, 150]) == MARK_COLOUR


def test_the_circle_follows_the_mouse_until_the_second_click():
    tool, _ = labeller()
    tool.click(*on_picture(100, 200))

    tool.move(*on_picture(100, 140))

    assert tuple(tool.render()[BANNER + 260, 100]) == MARK_COLOUR
    assert tool.result() is None


# --- one clip -------------------------------------------------------------------

WIDTH, HEIGHT, RADIUS, FRAMES = 720, 1280, 96.0, 40


def clip_script(centres, picks, scale, extra=()):
    script = []
    for index in picks:
        cx, cy = centres[index]
        script.append(("click", round(cx * scale), round(cy * scale) + BANNER))
        script.append(("move", round(cx * scale), round((cy - RADIUS / 2) * scale) + BANNER))
        script.append(("click", round(cx * scale), round((cy - RADIUS) * scale) + BANNER))
        script.append(("key", SPACE))
    return [*script, *extra]


def test_a_clip_is_labelled_on_frames_spread_over_it(tmp_path):
    frames, centres = squat_scenes(WIDTH, HEIGHT, FRAMES, RADIUS)
    clip = write_clip(tmp_path / "clip.mp4", frames)
    picks = pick_frames(FRAMES)
    window = ScriptedWindow(clip_script(centres, picks, scale=0.5))

    labels = label_tool.label_clip(window, clip, wanted=5, max_width=1600, max_height=640, position="clip 1/1")

    assert [label.frame for label in labels] == picks == [4, 12, 20, 28, 36]
    assert window.script == []
    assert all(shape == (640 + BANNER, 360, 3) for shape in window.shown)
    for label, index in zip(labels, picks, strict=True):
        cx, cy = centres[index]
        assert label.file == "clip.mp4"
        assert (label.video_width, label.video_height) == (WIDTH, HEIGHT)
        assert label.t == pytest.approx(index / 30, abs=0.002)
        # A click lands on a whole window pixel, which is two video pixels here.
        assert label.centre == pytest.approx((cx, cy), abs=1.5)
        assert label.radius_px == pytest.approx(RADIUS, abs=2.0)


def test_h_records_a_frame_where_the_plate_is_hidden(tmp_path):
    frames, centres = squat_scenes(WIDTH, HEIGHT, FRAMES, RADIUS)
    clip = write_clip(tmp_path / "clip.mp4", frames)
    picks = pick_frames(FRAMES)
    script = clip_script(centres, picks[:4], scale=0.5, extra=[("key", ord("h")), ("key", SPACE)])

    labels = label_tool.label_clip(
        ScriptedWindow(script), clip, wanted=5, max_width=1600, max_height=640, position="clip 1/1"
    )

    assert [label.visible for label in labels] == [True, True, True, True, False]
    assert labels[4].frame == picks[4]


def test_stopping_part_way_gives_no_labels_for_the_clip(tmp_path):
    frames, centres = squat_scenes(WIDTH, HEIGHT, FRAMES, RADIUS)
    clip = write_clip(tmp_path / "clip.mp4", frames)
    script = clip_script(centres, pick_frames(FRAMES)[:2], scale=0.5, extra=[("key", ord("q"))])

    labels = label_tool.label_clip(
        ScriptedWindow(script), clip, wanted=5, max_width=1600, max_height=640, position="clip 1/1"
    )

    assert labels is None


def test_closing_the_window_stops_like_q(tmp_path):
    frames, _ = squat_scenes(WIDTH, HEIGHT, FRAMES, RADIUS)
    clip = write_clip(tmp_path / "clip.mp4", frames)
    window = ScriptedWindow([("key", NO_KEY)])
    window.is_closed = lambda: True

    assert label_tool.label_clip(window, clip, wanted=5, max_width=1600, max_height=640, position="clip 1/1") is None


# --- the command ------------------------------------------------------------------

SMALL = (360, 640, 48.0, 20)


@pytest.fixture
def clips(tmp_path):
    """A folder with two short clips, and each clip's plate centres."""
    width, height, radius, count = SMALL
    folder = tmp_path / "clips"
    folder.mkdir()
    frames, centres = squat_scenes(width, height, count, radius)
    for name in ("a.mp4", "b.mp4"):
        write_clip(folder / name, frames)
    return folder, centres


def small_script(centres, frames=5):
    radius, count = SMALL[2], SMALL[3]
    script = []
    for index in pick_frames(count)[:frames]:
        cx, cy = centres[index]
        script += [("click", round(cx), round(cy) + BANNER), ("click", round(cx), round(cy - radius) + BANNER)]
        script.append(("key", ENTER))
    return script


def run(monkeypatch, script, *arguments):
    window = ScriptedWindow(script)
    monkeypatch.setattr(label_tool, "CvWindow", lambda title: window)
    return label_tool.main([str(a) for a in arguments]), window


def test_labels_every_clip_and_writes_the_file_beside_them(clips, monkeypatch):
    folder, centres = clips

    code, window = run(monkeypatch, small_script(centres) * 2, folder)

    assert code == 0
    assert window.was_closed
    labels = read_labels(folder / "plate-labels.csv")
    assert [(label.file, label.frame) for label in labels] == [
        *[("a.mp4", i) for i in pick_frames(20)],
        *[("b.mp4", i) for i in pick_frames(20)],
    ]
    assert all(label.radius_px == pytest.approx(48.0, abs=1.0) for label in labels)
    # Numbers only: the tool never saves a picture.
    assert sorted(p.name for p in folder.iterdir()) == ["a.mp4", "b.mp4", "plate-labels.csv"]


def test_stopping_keeps_finished_clips_and_the_next_run_carries_on(clips, monkeypatch, capsys):
    folder, centres = clips

    code, _ = run(monkeypatch, [*small_script(centres), *small_script(centres, frames=2), ("key", ord("q"))], folder)

    assert code == 0
    assert {label.file for label in read_labels(folder / "plate-labels.csv")} == {"a.mp4"}
    assert "1 of 2" in capsys.readouterr().out

    # The second run asks only for b.mp4: a script for one clip is enough.
    code, window = run(monkeypatch, small_script(centres), folder)

    assert code == 0
    assert window.script == []
    labels = read_labels(folder / "plate-labels.csv")
    assert [label.file for label in labels] == ["a.mp4"] * 5 + ["b.mp4"] * 5
    assert "2 of 2" in capsys.readouterr().out


def test_nothing_to_do_when_every_clip_is_labelled(clips, monkeypatch):
    folder, centres = clips
    run(monkeypatch, small_script(centres) * 2, folder)

    code, window = run(monkeypatch, [], folder)

    assert code == 0
    assert window.shown == []


def test_only_relabels_the_named_clip(clips, monkeypatch):
    folder, centres = clips
    run(monkeypatch, small_script(centres) * 2, folder)

    code, _ = run(monkeypatch, [("key", ord("h")), ("key", SPACE)] * 5, folder, "--only", "a.mp4")

    assert code == 0
    labels = read_labels(folder / "plate-labels.csv")
    assert [label.visible for label in labels if label.file == "a.mp4"] == [False] * 5
    assert [label.visible for label in labels if label.file == "b.mp4"] == [True] * 5


def test_out_and_frames_options(clips, monkeypatch, tmp_path):
    folder, centres = clips
    out = tmp_path / "elsewhere" / "labels.csv"

    code, _ = run(monkeypatch, small_script(centres, frames=3) * 2, folder, "--out", out, "--frames", "3")

    assert code == 0
    assert [label.frame for label in read_labels(out)] == pick_frames(20, wanted=3) * 2
    assert not (folder / "plate-labels.csv").exists()


def test_frames_must_be_at_least_one(clips, monkeypatch):
    folder, _ = clips

    with pytest.raises(SystemExit) as exit_info:
        run(monkeypatch, [], folder, "--frames", "0")

    assert exit_info.value.code == 2


def test_a_folder_without_clips_is_an_error(tmp_path, monkeypatch, capsys):
    code, window = run(monkeypatch, [], tmp_path)

    assert code == 1
    assert "no clips" in capsys.readouterr().err
    assert window.shown == []


def test_only_must_name_a_clip_in_the_folder(clips, monkeypatch, capsys):
    folder, _ = clips

    code, _ = run(monkeypatch, [], folder, "--only", "z.mp4")

    assert code == 1
    assert "z.mp4" in capsys.readouterr().err


def test_a_file_that_is_not_a_video_is_reported_and_the_rest_still_labelled(clips, monkeypatch, capsys):
    folder, centres = clips
    (folder / "broken.mp4").write_text("not a video")

    code, _ = run(monkeypatch, small_script(centres) * 2, folder)

    assert code == 1
    assert "broken.mp4" in capsys.readouterr().err
    assert {label.file for label in read_labels(folder / "plate-labels.csv")} == {"a.mp4", "b.mp4"}
