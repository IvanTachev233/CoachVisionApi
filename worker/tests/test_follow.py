"""Following a marked plate, on synthetic clips. No pose is given anywhere: the follower must not need one."""

import json
import statistics

import cv2
import numpy as np
import pytest
from conftest import render_scene, squat_scenes, write_clip

from vision_worker import cli, models
from vision_worker.bar import BAR_DETECTOR, MARKED_DETECTOR, BarTracker, marked_detector

WIDTH, HEIGHT = 720, 1280
RADIUS = 96.0
FRAMES = 60
JOB_ID = "job_01JC8YX4D2F7H1K9M3P5Q8S0TV"


class PlainTracker(BarTracker):
    """The follower with both options off, as the tests of the plain follower need it."""

    def follow(self, index, centre, rim, *, colour=False, partial_rim=False):
        return super().follow(index, centre, rim, colour=colour, partial_rim=partial_rim)


def tracker_for(frames):
    tracker = PlainTracker(WIDTH, HEIGHT)
    for frame in frames:
        tracker.add(frame, None)
    return tracker


def rim_above(centre, radius=RADIUS):
    return (centre[0], centre[1] - radius)


def errors(bars, centres):
    return [max(abs(bar.x - cx), abs(bar.y - cy)) for bar, (cx, cy) in zip(bars, centres) if bar is not None]


def test_the_marked_detector_has_its_own_name():
    assert MARKED_DETECTOR != BAR_DETECTOR
    assert MARKED_DETECTOR.startswith("opencv-")
    assert MARKED_DETECTOR.rsplit("@", 1)[1].isdigit()


@pytest.mark.parametrize("marked", [0, 22, 31, FRAMES - 1], ids=["first", "moving", "middle", "last"])
def test_follows_a_marked_plate_through_the_reps_forwards_and_backwards(marked):
    frames, centres = squat_scenes(WIDTH, HEIGHT, FRAMES, RADIUS)

    bars = tracker_for(frames).follow(marked, centres[marked], rim_above(centres[marked]))

    assert len(bars) == FRAMES
    assert sum(bar is not None for bar in bars) >= 0.9 * FRAMES
    assert bars[marked] is not None
    assert statistics.median(errors(bars, centres)) <= 4.0
    assert max(errors(bars, centres)) <= 12.0
    found = [bar for bar in bars if bar is not None]
    assert statistics.median(bar.radius_px for bar in found) == pytest.approx(RADIUS, rel=0.08)
    assert all(0.0 <= bar.confidence <= 1.0 for bar in found)


def test_a_rough_mark_still_finds_the_plate_and_measures_it():
    frames, centres = squat_scenes(WIDTH, HEIGHT, FRAMES, RADIUS)
    cx, cy = centres[30]
    centre = (cx + 0.12 * RADIUS, cy - 0.1 * RADIUS)

    bars = tracker_for(frames).follow(30, centre, rim_above(centre, RADIUS * 1.12))

    assert sum(bar is not None for bar in bars) >= 0.9 * FRAMES
    assert statistics.median(errors(bars, centres)) <= 4.0
    assert statistics.median(bar.radius_px for bar in bars if bar is not None) == pytest.approx(RADIUS, rel=0.08)


def test_a_plate_seen_at_an_angle_is_followed_and_reports_its_longer_radius():
    frames, centres = squat_scenes(WIDTH, HEIGHT, FRAMES, RADIUS, squash=0.72)

    bars = tracker_for(frames).follow(30, centres[30], rim_above(centres[30]))

    assert sum(bar is not None for bar in bars) >= 0.8 * FRAMES
    assert statistics.median(errors(bars, centres)) <= 5.0
    assert statistics.median(bar.radius_px for bar in bars if bar is not None) == pytest.approx(RADIUS, rel=0.1)


def test_a_mark_on_empty_background_gives_no_positions():
    frames, _ = squat_scenes(WIDTH, HEIGHT, FRAMES, RADIUS)
    nowhere = (WIDTH * 0.2, HEIGHT * 0.85)

    bars = tracker_for(frames).follow(30, nowhere, rim_above(nowhere))

    assert bars == [None] * FRAMES


def test_a_mark_in_a_clip_without_the_plate_gives_no_positions():
    frames, centres = squat_scenes(WIDTH, HEIGHT, FRAMES, RADIUS, plate=False)

    bars = tracker_for(frames).follow(30, centres[30], rim_above(centres[30]))

    assert bars == [None] * FRAMES


def test_follows_the_plate_that_was_marked_not_the_one_that_moves():
    # The plate stored on the rack is marked: it never moves, and the loaded plate passing by must not take over.
    frames, _ = squat_scenes(WIDTH, HEIGHT, FRAMES, RADIUS)
    stored, stored_radius = (int(WIDTH * 0.82), int(HEIGHT * 0.3)), int(WIDTH * 0.085)

    bars = tracker_for(frames).follow(10, stored, rim_above(stored, stored_radius))

    found = [bar for bar in bars if bar is not None]
    assert len(found) >= 0.9 * FRAMES
    assert all(abs(bar.x - stored[0]) <= 4 and abs(bar.y - stored[1]) <= 4 for bar in found)
    assert statistics.median(bar.radius_px for bar in found) == pytest.approx(stored_radius, rel=0.1)


def test_frames_where_the_plate_is_covered_get_no_position_and_it_is_found_again():
    frames, centres = squat_scenes(WIDTH, HEIGHT, FRAMES, RADIUS)
    for i in range(26, 31):
        frames[i] = render_scene(WIDTH, HEIGHT, None, seed=7 + i % 3)

    bars = tracker_for(frames).follow(5, centres[5], rim_above(centres[5]))

    assert all(bars[i] is None for i in range(26, 31))
    assert sum(bar is not None for bar in bars[31:]) >= 0.8 * (FRAMES - 31)
    assert statistics.median(errors(bars, centres)) <= 4.0


def test_a_mark_outside_the_clip_or_the_picture_is_refused():
    frames, centres = squat_scenes(WIDTH, HEIGHT, 8, RADIUS)
    tracker = tracker_for(frames)

    with pytest.raises(ValueError, match="frame"):
        tracker.follow(8, centres[0], rim_above(centres[0]))
    with pytest.raises(ValueError, match="same point"):
        tracker.follow(0, centres[0], centres[0])


def test_no_frames_is_not_an_error_for_the_automatic_finder_still():
    assert BarTracker(WIDTH, HEIGHT).finish() == []


# --- the command -----------------------------------------------------------------


class NoPose:
    def detect(self, image, t):
        return None

    def close(self):
        pass


@pytest.fixture
def no_pose(tmp_path, monkeypatch):
    """A model folder with placeholder files and a pose model that never finds a body."""
    folder = tmp_path / "models"
    folder.mkdir()
    for name in models.MODEL_FILES.values():
        (folder / name).write_bytes(b"placeholder")
    monkeypatch.setattr(cli, "PoseLandmarkerEstimator", lambda model_file: NoPose())
    monkeypatch.setattr(models, "verify", lambda variant, path: None)
    return folder


def run(clip, out, model_dir, *mark):
    """`mark` is the five numbers of --mark, then any further options."""
    options = ["--mark", *map(str, mark[:5]), *mark[5:]] if mark else []
    return cli.main([str(clip), "--out", str(out), "--model-dir", str(model_dir), "--job-id", JOB_ID, *options])


def test_mark_option_follows_the_plate_without_a_pose_and_names_the_marked_detector(
    tmp_path, no_pose, raw_output_validator
):
    frames, centres = squat_scenes(WIDTH, HEIGHT, 45, RADIUS)
    clip = write_clip(tmp_path / "clip.mp4", frames)
    out = tmp_path / "result.json"
    cx, cy = centres[20]

    # Frame 20 of a 30 frames-a-second clip is at two thirds of a second.
    assert run(clip, out, no_pose, 20 / 30, cx, cy, cx, cy - RADIUS) == 0

    result = json.loads(out.read_text())
    assert list(raw_output_validator.iter_errors(result)) == []
    assert result["worker"]["barDetector"] == MARKED_DETECTOR + "+colour+partial-rim"
    assert all(frame["pose"] is None for frame in result["frames"])
    bars = [(frame["bar"], centre) for frame, centre in zip(result["frames"], centres) if frame["bar"]]
    assert len(bars) >= 0.8 * 45
    assert all(abs(bar["x"] - x) <= 14 and abs(bar["y"] - y) <= 14 for bar, (x, y) in bars)


def test_without_a_mark_the_circle_finder_is_named(tmp_path, no_pose, raw_output_validator):
    frames, _ = squat_scenes(WIDTH, HEIGHT, 20, RADIUS)
    clip = write_clip(tmp_path / "clip.mp4", frames)
    out = tmp_path / "result.json"

    assert cli.main([str(clip), "--out", str(out), "--model-dir", str(no_pose), "--finder", "shoulders"]) == 0

    result = json.loads(out.read_text())
    assert result["worker"]["barDetector"] == BAR_DETECTOR
    assert all(frame["bar"] is None for frame in result["frames"])


def test_a_mark_on_empty_background_gives_valid_output_without_positions(tmp_path, no_pose, raw_output_validator):
    frames, _ = squat_scenes(WIDTH, HEIGHT, 30, RADIUS)
    out = tmp_path / "result.json"
    x, y = WIDTH * 0.2, HEIGHT * 0.85

    assert run(write_clip(tmp_path / "clip.mp4", frames), out, no_pose, 0.5, x, y, x, y - RADIUS) == 0

    result = json.loads(out.read_text())
    assert list(raw_output_validator.iter_errors(result)) == []
    assert result["worker"]["barDetector"] == MARKED_DETECTOR + "+colour+partial-rim"
    assert all(frame["bar"] is None for frame in result["frames"])


def test_a_mark_at_a_time_the_video_does_not_have_is_an_error(tmp_path, no_pose, capsys):
    frames, centres = squat_scenes(WIDTH, HEIGHT, 30, RADIUS)
    out = tmp_path / "result.json"
    cx, cy = centres[0]

    assert run(write_clip(tmp_path / "clip.mp4", frames), out, no_pose, 9.0, cx, cy, cx, cy - RADIUS) == 1

    assert "mark" in capsys.readouterr().err
    assert not out.exists()


def test_a_mark_needs_five_numbers(tmp_path, no_pose):
    with pytest.raises(SystemExit) as exit_info:
        run(tmp_path / "clip.mp4", tmp_path / "result.json", no_pose, 0.5, 100, 100)

    assert exit_info.value.code == 2


# --- options: follow by colour, follow a partly covered rim ------------------------


def tinted_scenes(frame_count=FRAMES):
    """A squat whose plate has the background's brightness and differs only in colour: grey pictures don't show it."""
    _, centres = squat_scenes(WIDTH, HEIGHT, frame_count, RADIUS)
    frames = []
    for i, (cx, cy) in enumerate(centres):
        lab = cv2.cvtColor(render_scene(WIDTH, HEIGHT, None, seed=7 + i % 3), cv2.COLOR_BGR2Lab)
        plate = np.zeros((HEIGHT, WIDTH), np.uint8)
        cv2.circle(plate, (round(cx), round(cy)), int(RADIUS), 255, -1, cv2.LINE_AA)
        lab[..., 1][plate > 127] = 128 + 45
        lab[..., 2][plate > 127] = 128 + 30
        frames.append(cv2.cvtColor(lab, cv2.COLOR_Lab2BGR))
    return frames, centres


def half_covered_scenes(covered_from=10):
    """A squat where, from frame `covered_from` on, something hides all of the plate but an arc on its left."""
    frames, centres = squat_scenes(WIDTH, HEIGHT, FRAMES, RADIUS)
    cover = render_scene(WIDTH, HEIGHT, None, seed=3)
    for i in range(covered_from, FRAMES):
        cx, cy = centres[i]
        left, right = int(cx - 0.35 * RADIUS), int(cx + 1.4 * RADIUS)
        top, bottom = int(cy - 1.4 * RADIUS), int(cy + 1.4 * RADIUS)
        frames[i][top:bottom, left:right] = cover[top:bottom, left:right]
    return frames, centres


def colour_tracker_for(frames):
    tracker = BarTracker(WIDTH, HEIGHT, colour=True)
    for frame in frames:
        tracker.add(frame, None)
    return tracker


def test_both_options_are_on_unless_turned_off():
    tinted, tinted_centres = tinted_scenes()
    covered, covered_centres = half_covered_scenes(covered_from=10)

    by_colour = colour_tracker_for(tinted).follow(30, tinted_centres[30], rim_above(tinted_centres[30]))
    by_arc = colour_tracker_for(covered).follow(5, covered_centres[5], rim_above(covered_centres[5]))

    assert sum(bar is not None for bar in by_colour) >= 0.9 * FRAMES
    assert sum(bar is not None for bar in by_arc[10:]) >= 0.8 * (FRAMES - 10)


def test_a_tracker_without_colours_follows_in_grey_unless_colour_is_asked_for():
    frames, centres = squat_scenes(WIDTH, HEIGHT, FRAMES, RADIUS)
    tracker = BarTracker(WIDTH, HEIGHT)
    for frame in frames:
        tracker.add(frame, None)

    bars = tracker.follow(30, centres[30], rim_above(centres[30]))

    assert sum(bar is not None for bar in bars) >= 0.9 * FRAMES


def test_the_options_are_named_in_the_detector():
    assert marked_detector() == MARKED_DETECTOR
    assert marked_detector(colour=True) == MARKED_DETECTOR + "+colour"
    assert marked_detector(partial_rim=True) == MARKED_DETECTOR + "+partial-rim"
    assert marked_detector(colour=True, partial_rim=True) == MARKED_DETECTOR + "+colour+partial-rim"


def test_colour_follows_a_plate_that_grey_pictures_do_not_show():
    frames, centres = tinted_scenes()
    mark = (30, centres[30], rim_above(centres[30]))

    without = colour_tracker_for(frames).follow(*mark, colour=False)
    bars = colour_tracker_for(frames).follow(*mark, colour=True)

    assert without == [None] * FRAMES
    assert sum(bar is not None for bar in bars) >= 0.9 * FRAMES
    assert statistics.median(errors(bars, centres)) <= 4.0
    assert statistics.median(bar.radius_px for bar in bars if bar is not None) == pytest.approx(RADIUS, rel=0.08)


def test_colour_still_follows_an_ordinary_plate():
    frames, centres = squat_scenes(WIDTH, HEIGHT, FRAMES, RADIUS)

    bars = colour_tracker_for(frames).follow(30, centres[30], rim_above(centres[30]), colour=True)

    assert sum(bar is not None for bar in bars) >= 0.9 * FRAMES
    assert statistics.median(errors(bars, centres)) <= 4.0


def test_colour_needs_a_tracker_that_kept_the_colours():
    frames, centres = squat_scenes(WIDTH, HEIGHT, 8, RADIUS)

    with pytest.raises(ValueError, match="colour"):
        tracker_for(frames).follow(0, centres[0], rim_above(centres[0]), colour=True)


def test_a_partly_covered_plate_is_lost_without_the_option_and_followed_with_it():
    frames, centres = half_covered_scenes(covered_from=10)
    mark = (5, centres[5], rim_above(centres[5]))

    without = tracker_for(frames).follow(*mark)
    bars = tracker_for(frames).follow(*mark, partial_rim=True)

    lost = sum(bar is not None for bar in without[10:])
    kept = sum(bar is not None for bar in bars[10:])
    assert lost <= 0.2 * (FRAMES - 10)
    # 0.6 and a gap of 25, not 0.75 (Ivan, 2026-11-14; 0.8 before 2026-11-11): the frame where the
    # follower loses this arc moves with the CPU's arithmetic, in OpenCV's drawing of the scene and
    # in the follower itself. Seen: 39 of the 50 on arm64, 33-34 on x86-64, 3 without the option on
    # both. What is tested is that the option is what keeps the plate.
    assert kept >= 0.6 * (FRAMES - 10)
    assert kept - lost >= 25
    assert statistics.median(errors(bars[10:], centres[10:])) <= 6.0
    assert statistics.median(bar.radius_px for bar in bars[10:] if bar is not None) == pytest.approx(RADIUS, rel=0.08)


def test_a_plate_with_its_own_side_showing_and_its_far_edge_hidden_stays_on_its_middle():
    # Seen from the side of the bar, a plate shows its thickness next to its face, in a shade of
    # the same colour, and the lifter's head hides the face's other edge: the rim alone puts the
    # circle between the two, a quarter of a radius off the hub.
    frames, centres = side_view_scenes()

    bars = colour_tracker_for(frames).follow(5, centres[5], rim_above(centres[5]))

    assert sum(bar is not None for bar in bars[10:]) >= 0.8 * (FRAMES - 10)
    assert statistics.median(errors(bars[10:], centres[10:])) <= 0.1 * RADIUS  # the rim alone: over 0.25


def side_view_scenes():
    """A squat seen from the side of the bar from frame 10 on: the plate's side shows and its far edge is hidden."""
    _, centres = squat_scenes(WIDTH, HEIGHT, FRAMES, RADIUS)
    frames = []
    for i, (cx, cy) in enumerate(centres):
        frame = render_scene(WIDTH, HEIGHT, (cx, cy, RADIUS, RADIUS), seed=8)
        if i >= 10:
            face = np.zeros((HEIGHT, WIDTH), np.uint8)
            cv2.circle(face, (round(cx), round(cy)), int(RADIUS), 255, -1)
            side = np.zeros((HEIGHT, WIDTH), np.uint8)
            cv2.circle(side, (round(cx - 0.3 * RADIUS), round(cy)), int(RADIUS), 255, -1)
            frame[(side > 0) & (face == 0)] = (32, 90, 37)
            cv2.circle(frame, (round(cx + 0.95 * RADIUS), round(cy)), int(0.5 * RADIUS), (120, 140, 170), -1)
        frames.append(frame)
    return frames, centres


def test_a_middle_that_is_hard_to_make_out_still_holds_the_circle_if_it_held_it_the_frame_before():
    # Lower in a lift the plate's middle looks less like it did at the mark (other light, a little
    # blur): here 0.5 to 0.7 like it. A circle that goes by the middle in some frames and by the
    # rim in others steps aside each time it changes over, so the middle is kept while it lasts.
    clean = render_scene(WIDTH, HEIGHT, (300.0, 500.0, RADIUS, RADIUS), seed=8)
    grain = cv2.resize(np.random.default_rng(11).normal(0, 100, size=(HEIGHT // 4, WIDTH // 4)), (WIDTH, HEIGHT))
    middle = np.zeros((HEIGHT, WIDTH), np.uint8)
    cv2.circle(middle, (300, 520), int(0.6 * RADIUS), 255, -1)
    grainy = render_scene(WIDTH, HEIGHT, (300.0, 520.0, RADIUS, RADIUS), seed=8)
    noisy = np.clip(grainy.astype(np.float64) + grain[..., None], 0, 255).astype(np.uint8)
    grainy[middle > 0] = noisy[middle > 0]
    tracker = tracker_for([clean, grainy])
    scale = tracker._scale
    radius = RADIUS * scale
    core = tracker._core(tracker._gray(0), 300 * scale, 500 * scale, radius, radius)
    views = [tracker._gray(1)]
    true = (300 * scale, 520 * scale)
    rim_says = (true[0] - 0.15 * radius, true[1], 1.0, 0.5)  # the rim's centre, pulled aside

    held, used_when_held = tracker._settle_on_core(views, core, rim_says, [true], radius, radius, 0, True)
    fresh, used_when_fresh = tracker._settle_on_core(views, core, rim_says, [true], radius, radius, 0, False)

    assert used_when_held and abs(held[0] - true[0]) <= 1.0 and abs(held[1] - true[1]) <= 1.0
    assert not used_when_fresh and fresh == rim_says


@pytest.mark.parametrize(
    ("colour", "partial_rim"), [(True, False), (False, True), (True, True)], ids=["colour", "partial-rim", "both"]
)
def test_the_options_do_not_make_up_a_plate(colour, partial_rim):
    frames, centres = squat_scenes(WIDTH, HEIGHT, FRAMES, RADIUS)
    nowhere = (WIDTH * 0.2, HEIGHT * 0.85)
    options = {"colour": colour, "partial_rim": partial_rim}

    assert colour_tracker_for(frames).follow(30, nowhere, rim_above(nowhere), **options) == [None] * FRAMES

    # A plate that is wholly covered for a while gets no position in those frames.
    for i in range(26, 31):
        frames[i] = render_scene(WIDTH, HEIGHT, None, seed=7 + i % 3)
    bars = colour_tracker_for(frames).follow(5, centres[5], rim_above(centres[5]), **options)
    assert all(bars[i] is None for i in range(26, 31))
    assert statistics.median(errors(bars, centres)) <= 4.0


@pytest.mark.parametrize(
    ("switches", "name"),
    [
        (["--follow-colour", "--follow-partial-rim"], "+colour+partial-rim"),
        (["--no-follow-colour"], "+partial-rim"),
        (["--no-follow-partial-rim"], "+colour"),
        (["--no-follow-colour", "--no-follow-partial-rim"], ""),
    ],
    ids=["both", "no-colour", "no-partial-rim", "neither"],
)
def test_each_follow_option_can_be_turned_off(switches, name, tmp_path, no_pose, raw_output_validator):
    frames, centres = squat_scenes(WIDTH, HEIGHT, 30, RADIUS)
    clip = write_clip(tmp_path / "clip.mp4", frames)
    out = tmp_path / "result.json"
    cx, cy = centres[15]

    assert run(clip, out, no_pose, 0.5, cx, cy, cx, cy - RADIUS, *switches) == 0

    result = json.loads(out.read_text())
    assert list(raw_output_validator.iter_errors(result)) == []
    assert result["worker"]["barDetector"] == MARKED_DETECTOR + name
    assert sum(frame["bar"] is not None for frame in result["frames"]) >= 0.8 * 30


@pytest.mark.parametrize("switch", ["--follow-colour", "--no-follow-colour", "--follow-partial-rim"])
def test_the_follow_options_need_a_mark(switch, tmp_path, no_pose):
    with pytest.raises(SystemExit) as exit_info:
        cli.main([str(tmp_path / "clip.mp4"), "--model-dir", str(no_pose), switch])

    assert exit_info.value.code == 2
