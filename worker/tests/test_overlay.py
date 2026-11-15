import json

import cv2
import numpy as np
import pytest
from conftest import CONTRACTS, fake_pose, squat_scenes, write_clip

from vision_worker import overlay
from vision_worker.bar import BarDetection
from vision_worker.overlay import vertical_speeds
from vision_worker.raw_output import build_document

WIDTH, HEIGHT, RADIUS, FRAMES = 360, 640, 48.0, 12


def clip_and_output(tmp_path, *, bar_until=FRAMES, frames_in_output=FRAMES):
    frames, centres = squat_scenes(WIDTH, HEIGHT, FRAMES, RADIUS)
    clip = write_clip(tmp_path / "clip.mp4", frames)
    rows = [
        (i, i / 30, BarDetection(cx, cy, RADIUS, 0.8) if i < bar_until else None, fake_pose((cx, cy), i))
        for i, (cx, cy) in enumerate(centres[:frames_in_output])
    ]
    document = build_document(
        job_id="job_01JC8YX4D2F7H1K9M3P5Q8S0TV",
        pose_model="mediapipe-pose-landmarker-full@test",
        video={"width": WIDTH, "height": HEIGHT, "frameRate": 30.0, "durationS": FRAMES / 30, "frameCount": FRAMES},
        frames=rows,
    )
    output = tmp_path / "clip.json"
    output.write_text(json.dumps(document))
    return clip, output, centres


def read_frames(path):
    capture = cv2.VideoCapture(str(path))
    frames = []
    while True:
        ok, frame = capture.read()
        if not ok:
            break
        frames.append(frame)
    capture.release()
    return frames


def is_marked(frame, x, y, colour):
    """Whether the pixels around (x, y) are close to the overlay's BGR colour (the encoder shifts them a little)."""
    patch = frame[int(y) - 1 : int(y) + 2, int(x) - 1 : int(x) + 2].reshape(-1, 3).mean(axis=0)
    return all(abs(float(patch[c]) - colour[c]) < 70 for c in range(3))


def test_draws_the_plate_where_the_output_says(tmp_path):
    clip, output, centres = clip_and_output(tmp_path)
    out = tmp_path / "overlays" / "clip.mp4"

    code = overlay.main([str(clip), str(output), str(out)])

    assert code == 0
    drawn = read_frames(out)
    assert len(drawn) == FRAMES
    assert drawn[0].shape == (HEIGHT, WIDTH, 3)
    cx, cy = centres[5]
    # The top of the rim and the centre dot carry the bar colour; the same spots in the source don't.
    assert is_marked(drawn[5], cx, cy - RADIUS, overlay.BAR_COLOUR)
    assert is_marked(drawn[5], cx, cy, overlay.BAR_COLOUR)
    assert not is_marked(read_frames(clip)[5], cx, cy - RADIUS, overlay.BAR_COLOUR)


def test_frames_without_a_bar_get_no_circle(tmp_path):
    clip, output, centres = clip_and_output(tmp_path, bar_until=4)
    out = tmp_path / "clip-overlay.mp4"

    assert overlay.main([str(clip), str(output), str(out)]) == 0

    drawn = read_frames(out)
    cx, cy = centres[8]
    assert not is_marked(drawn[8], cx, cy - RADIUS, overlay.BAR_COLOUR)
    assert is_marked(drawn[2], centres[2][0], centres[2][1] - RADIUS, overlay.BAR_COLOUR)


def test_large_video_is_shrunk(tmp_path):
    frames, _ = squat_scenes(720, 1280, 3, 96.0, plate=False)
    clip = write_clip(tmp_path / "big.mp4", frames)
    document = build_document(
        job_id="job_01JC8YX4D2F7H1K9M3P5Q8S0TV",
        pose_model="mediapipe-pose-landmarker-full@test",
        video={"width": 720, "height": 1280, "frameRate": 30.0, "durationS": 0.1, "frameCount": 3},
        frames=[(i, i / 30, None, None) for i in range(3)],
    )
    output = tmp_path / "big.json"
    output.write_text(json.dumps(document))
    out = tmp_path / "big-overlay.mp4"

    assert overlay.main([str(clip), str(output), str(out)]) == 0

    assert read_frames(out)[0].shape == (overlay.MAX_SIDE, overlay.MAX_SIDE * 720 // 1280, 3)


def test_output_from_another_video_is_refused(tmp_path, capsys):
    clip, output, _ = clip_and_output(tmp_path, frames_in_output=FRAMES - 3)
    out = tmp_path / "clip-overlay.mp4"

    code = overlay.main([str(clip), str(output), str(out)])

    assert code == 1
    assert "frames" in capsys.readouterr().err
    assert not out.exists()


def test_missing_video_fails_with_a_message(tmp_path, capsys):
    _, output, _ = clip_and_output(tmp_path)

    code = overlay.main([str(tmp_path / "absent.mp4"), str(output), str(tmp_path / "o.mp4")])

    assert code == 1
    assert "absent.mp4" in capsys.readouterr().err


def test_refuses_to_write_over_the_source_video(tmp_path):
    clip, output, _ = clip_and_output(tmp_path)

    with pytest.raises(SystemExit) as exit_info:
        overlay.main([str(clip), str(output), str(clip)])

    assert exit_info.value.code == 2


# --- joints and bones -------------------------------------------------------

GREY = 120
POSE = {
    "left_shoulder": {"x": 200.0, "y": 300.0, "confidence": 0.9},
    "right_shoulder": {"x": 120.0, "y": 300.0, "confidence": 0.9},
    "left_hip": {"x": 190.0, "y": 440.0, "confidence": 0.9},
    "right_hip": {"x": 130.0, "y": 440.0, "confidence": 0.9},
    # An elbow whose wrist the model didn't report.
    "left_elbow": {"x": 260.0, "y": 360.0, "confidence": 0.9},
}
BETWEEN_SHOULDERS = (160, 300)
DOWN_THE_LEFT_SIDE = (195, 370)  # midway from left shoulder to left hip
LEFT_SHOULDER = (200, 300)


def drawn(pose=POSE, shrink=1.0, **options):
    image = np.full((HEIGHT, WIDTH, 3), GREY, dtype=np.uint8)
    overlay.draw(image, {"index": 3, "bar": None, "pose": pose}, shrink, [], **options)
    return image


def has_colour(image, point, colour, reach=1):
    """Whether a pixel at or right next to `point` is `colour`, give or take the soft edge of a drawn line."""
    x, y = point
    patch = image[y - reach : y + reach + 1, x - reach : x + reach + 1].reshape(-1, 3).astype(int)
    return bool((np.abs(patch - np.array(colour)).max(axis=1) <= 40).any())


def distance_to(frame, point, colour):
    x, y = (round(v) for v in point)
    patch = frame[y - 1 : y + 2, x - 1 : x + 2].reshape(-1, 3).mean(axis=0)
    return float(np.abs(patch - np.array(colour)).max())


def test_bones_join_only_the_contracts_body_points():
    schema = json.loads((CONTRACTS / "v1.json").read_text())
    names = set(schema["$defs"]["pose"]["properties"])

    assert overlay.BONES
    assert all(start in names and end in names and start != end for start, end in overlay.BONES)
    assert len(set(map(frozenset, overlay.BONES))) == len(overlay.BONES)


def test_bones_are_drawn_between_joints_when_switched_on():
    image = drawn(bones=True)

    assert has_colour(image, BETWEEN_SHOULDERS, overlay.BONE_COLOUR)
    assert has_colour(image, DOWN_THE_LEFT_SIDE, overlay.BONE_COLOUR)


def test_bones_are_off_unless_asked_for():
    image = drawn()

    assert not has_colour(image, BETWEEN_SHOULDERS, overlay.BONE_COLOUR)
    assert tuple(image[BETWEEN_SHOULDERS[1], BETWEEN_SHOULDERS[0]]) == (GREY, GREY, GREY)


def test_a_bone_needs_both_of_its_joints():
    image = drawn(bones=True)

    # The elbow is there, its wrist is not: nothing leaves the elbow towards a guessed wrist.
    around_elbow = image[330:391, 230:291].reshape(-1, 3).astype(int)
    bone_pixels = (np.abs(around_elbow - np.array(overlay.BONE_COLOUR)).max(axis=1) <= 40).sum()
    shoulder_to_elbow_only = drawn(pose={k: POSE[k] for k in ("left_shoulder", "left_elbow")}, bones=True)
    expected = shoulder_to_elbow_only[330:391, 230:291].reshape(-1, 3).astype(int)
    assert bone_pixels > 0
    assert bone_pixels == (np.abs(expected - np.array(overlay.BONE_COLOUR)).max(axis=1) <= 40).sum()


def test_joints_are_drawn_by_default_and_sit_on_top_of_the_bones():
    assert has_colour(drawn(), LEFT_SHOULDER, overlay.JOINT_COLOUR, reach=0)
    assert has_colour(drawn(bones=True), LEFT_SHOULDER, overlay.JOINT_COLOUR, reach=0)


def test_joints_can_be_switched_off():
    image = drawn(joints=False)

    assert not has_colour(image, LEFT_SHOULDER, overlay.JOINT_COLOUR)
    assert tuple(image[LEFT_SHOULDER[1], LEFT_SHOULDER[0]]) == (GREY, GREY, GREY)


def test_bones_without_joints():
    image = drawn(joints=False, bones=True)

    assert has_colour(image, BETWEEN_SHOULDERS, overlay.BONE_COLOUR)
    assert not has_colour(image, LEFT_SHOULDER, overlay.JOINT_COLOUR)


def test_bones_follow_the_overlays_scale():
    image = drawn(shrink=0.5, bones=True)

    assert has_colour(image, (80, 150), overlay.BONE_COLOUR)
    assert not has_colour(image, BETWEEN_SHOULDERS, overlay.BONE_COLOUR)


def test_a_frame_without_a_pose_draws_no_bones():
    image = drawn(pose=None, bones=True)

    assert not has_colour(image, BETWEEN_SHOULDERS, overlay.BONE_COLOUR)


def test_bones_option_reaches_the_video(tmp_path):
    clip, output, centres = clip_and_output(tmp_path)
    pose = fake_pose(centres[5], 5)
    between = ((pose["left_shoulder"]["x"] + pose["right_shoulder"]["x"]) / 2, pose["left_shoulder"]["y"])

    assert overlay.main([str(clip), str(output), str(tmp_path / "plain.mp4")]) == 0
    assert overlay.main([str(clip), str(output), str(tmp_path / "bones.mp4"), "--bones"]) == 0

    plain = distance_to(read_frames(tmp_path / "plain.mp4")[5], between, overlay.BONE_COLOUR)
    bones = distance_to(read_frames(tmp_path / "bones.mp4")[5], between, overlay.BONE_COLOUR)
    assert bones < plain - 50


def test_no_joints_option_reaches_the_video(tmp_path):
    clip, output, centres = clip_and_output(tmp_path)
    shoulder = fake_pose(centres[5], 5)["left_shoulder"]

    assert overlay.main([str(clip), str(output), str(tmp_path / "plain.mp4")]) == 0
    assert overlay.main([str(clip), str(output), str(tmp_path / "bare.mp4"), "--no-joints"]) == 0

    assert is_marked(read_frames(tmp_path / "plain.mp4")[5], shoulder["x"], shoulder["y"], overlay.JOINT_COLOUR)
    assert not is_marked(read_frames(tmp_path / "bare.mp4")[5], shoulder["x"], shoulder["y"], overlay.JOINT_COLOUR)


def test_help_lists_both_switches(capsys):
    with pytest.raises(SystemExit) as exit_info:
        overlay.main(["--help"])

    assert exit_info.value.code == 0
    text = capsys.readouterr().out
    assert "--joints" in text and "--no-joints" in text
    assert "--bones" in text and "--no-bones" in text


def bar_frames(heights, radius=90.0, rate=30.0):
    return [
        {
            "index": i,
            "t": i / rate,
            "pose": None,
            "bar": None if y is None else {"x": 300.0, "y": y, "radiusPx": radius, "confidence": 0.5},
        }
        for i, y in enumerate(heights)
    ]


def test_speed_is_how_fast_the_plate_rises_taking_its_radius_as_225_mm():
    # 10 pixels up per frame at 30 frames a second, for a plate 90 pixels in radius: 10 / 90 * 0.225 m * 30.
    speeds = vertical_speeds(bar_frames([1000.0 - 10 * i for i in range(12)]))

    assert speeds[5] == pytest.approx(0.75, abs=0.01)
    assert all(speed == pytest.approx(0.75, abs=0.01) for speed in speeds)


def test_going_down_is_a_speed_below_zero_and_no_position_is_no_speed():
    heights = [500.0 + 4 * i for i in range(12)]
    heights[6] = None

    speeds = vertical_speeds(bar_frames(heights))

    assert speeds[2] == pytest.approx(-0.3, abs=0.01)
    assert speeds[6] is None
    assert speeds[5] is not None and speeds[7] is not None  # taken from the frames that are there
    assert vertical_speeds(bar_frames([500.0, None, None])) == [None, None, None]


def test_a_joint_the_model_is_unsure_of_is_drawn_fainter_and_so_is_a_bone_that_ends_in_it():
    image = np.zeros((400, 400, 3), np.uint8)
    frame = {
        "index": 0,
        "t": 0.0,
        "bar": None,
        "pose": {
            "left_shoulder": {"x": 100.0, "y": 100.0, "confidence": 0.9},
            "left_elbow": {"x": 100.0, "y": 200.0, "confidence": 0.9},
            "left_wrist": {"x": 100.0, "y": 300.0, "confidence": 0.2},
        },
    }

    overlay.draw(image, frame, 1.0, [], joints=True, bones=True)

    def brightness(x, y):
        return int(image[y, x].astype(int).sum())

    assert tuple(int(v) for v in image[200, 100]) == overlay.JOINT_COLOUR
    assert 0 < brightness(100, 300) < brightness(100, 200)  # the unsure wrist against the sure elbow
    assert tuple(int(v) for v in image[150, 100]) == overlay.BONE_COLOUR  # upper arm, both ends sure
    assert 0 < brightness(100, 250) < brightness(100, 150)  # forearm, one end unsure
