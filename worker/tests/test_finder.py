"""Finding the plate from a proposer's boxes, on synthetic clips. The proposer is faked: no model is used."""

import statistics

import cv2
import numpy as np
import pytest
from conftest import FakePose, fake_pose, render_scene, squat_scenes, write_clip

from vision_worker.bar import (
    BAR_DETECTOR,
    MAX_SIZE_SPREAD,
    BarDetection,
    BarTracker,
    proposed_detector,
)
from vision_worker.pipeline import analyse
from vision_worker.video import VideoReader

WIDTH, HEIGHT = 720, 1280
RADIUS = 96.0
FRAMES = 60
PICKED = (15, 30, 45)  # the frames a 60-frame clip is looked at: a quarter, half and three quarters of the way
STORED, STORED_RADIUS = (int(WIDTH * 0.82), int(HEIGHT * 0.3)), int(WIDTH * 0.085)
NOWHERE = (WIDTH * 0.2, HEIGHT * 0.85)


def box(centre, radius, score=0.4, squash=1.0):
    """A box around a circle or upright oval, in the video's pixels, with the proposer's score."""
    cx, cy = centre
    return (cx - radius * squash, cy - radius, cx + radius * squash, cy + radius, score)


class FakeProposer:
    """Replays boxes: `boxes(frame number)` gives the boxes, in the video's pixels, for a looked-at frame."""

    name = "fake-proposer@1"

    def __init__(self, boxes):
        self._boxes = boxes
        self.calls = 0
        self.sizes = []

    def propose(self, image):
        frame = PICKED[self.calls]
        self.calls += 1
        self.sizes.append(image.shape)
        scale = image.shape[0] / HEIGHT
        return [(x0 * scale, y0 * scale, x1 * scale, y1 * scale, score) for x0, y0, x1, y1, score in self._boxes(frame)]


def tracker_for(frames, poses, colour=True):
    tracker = BarTracker(WIDTH, HEIGHT, colour=colour)
    for frame, pose in zip(frames, poses):
        tracker.add(frame, pose)
    return tracker


def scene(**options):
    frames, centres = squat_scenes(WIDTH, HEIGHT, FRAMES, RADIUS, **options)
    return frames, centres, [fake_pose(centre, i) for i, centre in enumerate(centres)]


def errors(bars, centres):
    return [max(abs(bar.x - cx), abs(bar.y - cy)) for bar, (cx, cy) in zip(bars, centres) if bar is not None]


def test_finds_the_loaded_plate_among_the_boxes_and_follows_it():
    frames, centres, poses = scene()
    proposer = FakeProposer(
        lambda i: [box(STORED, STORED_RADIUS, 0.6), box(NOWHERE, RADIUS, 0.5), box(centres[i], RADIUS * 1.05, 0.3)]
    )

    bars = tracker_for(frames, poses).find(proposer)

    assert len(bars) == FRAMES
    assert sum(bar is not None for bar in bars) >= 0.9 * FRAMES
    assert statistics.median(errors(bars, centres)) <= 4.0
    assert statistics.median(bar.radius_px for bar in bars if bar is not None) == pytest.approx(RADIUS, rel=0.08)


def test_the_proposer_sees_three_frames_of_the_working_picture_in_colour():
    frames, centres, poses = scene()
    proposer = FakeProposer(lambda i: [box(centres[i], RADIUS)])

    tracker_for(frames, poses).find(proposer)

    assert proposer.calls == 3
    assert all(len(size) == 3 and size[2] == 3 and max(size[:2]) == 640 for size in proposer.sizes)


def test_a_plate_that_does_not_move_with_the_lifter_is_not_the_bar():
    frames, _, poses = scene()
    proposer = FakeProposer(lambda i: [box(STORED, STORED_RADIUS, 0.9)])

    assert tracker_for(frames, poses).find(proposer) == [None] * FRAMES


def test_the_plate_is_found_when_the_shoulders_do_not_move_with_it():
    # In a clean or a snatch the bar doesn't travel with the shoulders: here they stay where they were.
    frames, centres, _ = scene()
    poses = [fake_pose(centres[0], i) for i in range(FRAMES)]
    proposer = FakeProposer(lambda i: [box(STORED, STORED_RADIUS, 0.6), box(centres[i], RADIUS, 0.3)])

    bars = tracker_for(frames, poses).find(proposer)

    assert sum(bar is not None for bar in bars) >= 0.9 * FRAMES
    assert statistics.median(errors(bars, centres)) <= 4.0


def test_a_round_thing_that_hardly_moves_is_not_the_plate():
    # A shoe: round, the right size for a box, and it shifts a little in step with the lift.
    _, centres, poses = scene(plate=False)
    top = centres[0][1]
    shoes = [(WIDTH * 0.4, HEIGHT * 0.86 + 0.04 * (cy - top)) for _, cy in centres]
    frames = []
    for i, (x, y) in enumerate(shoes):
        frame = render_scene(WIDTH, HEIGHT, None, seed=7 + i % 3)
        cv2.circle(frame, (round(x), round(y)), 60, (30, 30, 30), -1, cv2.LINE_AA)
        frames.append(frame)
    proposer = FakeProposer(lambda i: [box(shoes[i], 60, 0.9)])

    assert tracker_for(frames, poses).find(proposer) == [None] * FRAMES


def test_a_moving_plate_far_from_the_lifter_is_not_theirs():
    # Somebody else's bar going up and down at the other side of the picture.
    frames, centres, _ = scene()
    poses = [fake_pose((x + 400, y + 250), i) for i, (x, y) in enumerate(centres)]
    proposer = FakeProposer(lambda i: [box(centres[i], RADIUS, 0.9)])

    assert tracker_for(frames, poses).find(proposer) == [None] * FRAMES


def test_a_track_whose_size_keeps_changing_is_not_a_plate():
    # A plate keeps its size through a lift; a circle that grows and shrinks is following something else.
    def track(sizes):
        return [BarDetection(x=300.0, y=400.0 + 5 * i, radius_px=size, confidence=0.6) for i, size in enumerate(sizes)]

    assert BarTracker._size_spread(track([100, 102, 99, 101, 98, 103] * 5)) < MAX_SIZE_SPREAD
    assert BarTracker._size_spread(track([70, 80, 95, 110, 120, 100] * 5)) > MAX_SIZE_SPREAD


def test_boxes_without_a_plate_in_them_give_no_positions():
    frames, _, poses = scene()
    proposer = FakeProposer(lambda i: [box(NOWHERE, RADIUS, 0.9), box((WIDTH * 0.3, HEIGHT * 0.7), RADIUS * 0.8, 0.9)])

    assert tracker_for(frames, poses).find(proposer) == [None] * FRAMES


def test_no_boxes_or_a_clip_without_the_plate_give_no_positions():
    frames, centres, poses = scene()
    assert tracker_for(frames, poses).find(FakeProposer(lambda i: [])) == [None] * FRAMES

    frames, centres, poses = scene(plate=False)
    proposer = FakeProposer(lambda i: [box(centres[i], RADIUS, 0.9)])
    assert tracker_for(frames, poses).find(proposer) == [None] * FRAMES


def test_boxes_of_the_wrong_shape_size_or_score_are_left_out():
    frames, centres, poses = scene()

    def boxes(i):
        cx, cy = centres[i]
        return [
            (cx - 3 * RADIUS, cy - RADIUS, cx + 3 * RADIUS, cy + RADIUS, 0.9),  # three times as wide as tall
            box(centres[i], RADIUS, 0.05),  # the proposer hardly believes it
            box(centres[i], RADIUS * 0.2, 0.9),  # far too small for a plate beside this lifter
            box(centres[i], RADIUS * 2.0, 0.9),  # far too big
        ]

    assert tracker_for(frames, poses).find(FakeProposer(boxes)) == [None] * FRAMES


def test_of_circles_about_one_centre_the_plate_s_rim_wins_over_its_inner_ring():
    frames, centres, poses = scene()
    proposer = FakeProposer(lambda i: [box(centres[i], RADIUS * 0.8, 0.9), box(centres[i], RADIUS, 0.2)])

    bars = tracker_for(frames, poses).find(proposer)

    assert sum(bar is not None for bar in bars) >= 0.9 * FRAMES
    assert statistics.median(bar.radius_px for bar in bars if bar is not None) == pytest.approx(RADIUS, rel=0.08)


def test_a_smaller_plate_in_front_gives_the_positions_and_the_plate_behind_it_the_size(monkeypatch):
    # Only one box is followed in the first round, and the small plate in front has the better score.
    monkeypatch.setattr("vision_worker.bar.FIND_FOLLOWS", 1)
    frames, centres, poses = scene()
    for frame, (cx, cy) in zip(frames, centres):
        cv2.circle(frame, (round(cx), round(cy)), int(RADIUS * 0.55), (25, 25, 25), -1, cv2.LINE_AA)
        cv2.circle(frame, (round(cx), round(cy)), int(RADIUS * 0.15), (185, 185, 185), -1, cv2.LINE_AA)
    proposer = FakeProposer(lambda i: [box(centres[i], RADIUS * 0.55, 0.9), box(centres[i], RADIUS, 0.2)])

    bars = tracker_for(frames, poses).find(proposer)

    assert sum(bar is not None for bar in bars) >= 0.9 * FRAMES
    assert statistics.median(errors(bars, centres)) <= 4.0
    assert statistics.median(bar.radius_px for bar in bars if bar is not None) == pytest.approx(RADIUS, rel=0.08)


def test_a_plate_as_bright_as_what_is_behind_it_is_found_by_its_colour():
    # The plate differs from the background in colour only: the grey picture has no rim to measure in the box.
    _, centres, poses = scene()
    frames = []
    for i, (cx, cy) in enumerate(centres):
        lab = cv2.cvtColor(render_scene(WIDTH, HEIGHT, None, seed=7 + i % 3), cv2.COLOR_BGR2Lab)
        plate = np.zeros((HEIGHT, WIDTH), np.uint8)
        cv2.circle(plate, (round(cx), round(cy)), int(RADIUS), 255, -1, cv2.LINE_AA)
        lab[..., 1][plate > 127] = 128 + 45
        lab[..., 2][plate > 127] = 128 + 30
        frames.append(cv2.cvtColor(lab, cv2.COLOR_Lab2BGR))
    proposer = FakeProposer(lambda i: [box(centres[i], RADIUS, 0.4)])

    bars = tracker_for(frames, poses).find(proposer)

    assert sum(bar is not None for bar in bars) >= 0.9 * FRAMES
    assert statistics.median(errors(bars, centres)) <= 4.0
    assert statistics.median(bar.radius_px for bar in bars if bar is not None) == pytest.approx(RADIUS, rel=0.08)


def test_frames_the_first_pass_lost_are_filled_by_following_again_from_the_edge_of_the_gap(monkeypatch):
    # The first time through, the follower loses the plate for ten frames it could have found:
    # here it is made to, by blinding it there once.
    frames, centres, poses = scene()
    tracker = tracker_for(frames, poses)
    follow, calls = BarTracker.follow, []

    def blind_once(self, index, centre, rim, **options):
        bars = follow(self, index, centre, rim, **options)
        calls.append(index)
        if len(calls) == 1:
            bars[22:32] = [None] * 10
        return bars

    monkeypatch.setattr(BarTracker, "follow", blind_once)

    bars = tracker.find(FakeProposer(lambda i: [box(centres[i], RADIUS, 0.4)]))

    assert calls[1] in (21, 32)  # followed again from a frame next to the gap
    assert all(bar is not None for bar in bars[22:32])
    assert statistics.median(errors(bars, centres)) <= 4.0
    assert max(errors(bars[22:32], centres[22:32])) <= 12.0


def test_an_oval_plate_is_found_from_a_tall_box():
    frames, centres, poses = scene(squash=0.72)
    proposer = FakeProposer(lambda i: [box(centres[i], RADIUS, 0.4, squash=0.72)])

    bars = tracker_for(frames, poses).find(proposer)

    assert sum(bar is not None for bar in bars) >= 0.8 * FRAMES
    assert statistics.median(bar.radius_px for bar in bars if bar is not None) == pytest.approx(RADIUS, rel=0.1)


def test_without_a_pose_nothing_is_reported():
    frames, centres, _ = scene()
    proposer = FakeProposer(lambda i: [box(centres[i], RADIUS, 0.9)])

    assert tracker_for(frames, [None] * FRAMES).find(proposer) == [None] * FRAMES


def test_finding_needs_a_tracker_that_kept_the_colours_and_no_frames_is_not_an_error():
    frames, _, poses = scene()

    with pytest.raises(ValueError, match="colour"):
        tracker_for(frames, poses, colour=False).find(FakeProposer(lambda i: []))
    assert BarTracker(WIDTH, HEIGHT, colour=True).find(FakeProposer(lambda i: [])) == []


# --- in the pipeline --------------------------------------------------------------


def test_the_pipeline_finds_by_proposals_when_given_a_proposer_and_names_it(tmp_path, raw_output_validator):
    frames, centres, poses = scene()
    clip = write_clip(tmp_path / "clip.mp4", frames)
    proposer = FakeProposer(lambda i: [box(STORED, STORED_RADIUS, 0.6), box(centres[i], RADIUS, 0.3)])

    with VideoReader(clip) as reader:
        document = analyse(
            reader, FakePose(poses), job_id="job_01JC8YX4D2F7H1K9M3P5Q8S0TV", pose_model="fake", proposer=proposer
        )

    assert list(raw_output_validator.iter_errors(document)) == []
    assert document["worker"]["barDetector"] == proposed_detector(proposer) == "fake-proposer@1+opencv-follow@1"
    assert document["worker"]["barDetector"] != BAR_DETECTOR
    bars = [(frame["bar"], centre) for frame, centre in zip(document["frames"], centres) if frame["bar"]]
    assert len(bars) >= 0.8 * FRAMES
    assert all(abs(bar["x"] - x) <= 14 and abs(bar["y"] - y) <= 14 for bar, (x, y) in bars)


def test_the_pipeline_with_a_proposer_and_no_plate_gives_valid_output_without_positions(tmp_path, raw_output_validator):
    frames, centres, poses = scene(plate=False)
    clip = write_clip(tmp_path / "clip.mp4", frames)
    proposer = FakeProposer(lambda i: [box(centres[i], RADIUS, 0.9), box(STORED, STORED_RADIUS, 0.9)])

    with VideoReader(clip) as reader:
        document = analyse(
            reader, FakePose(poses), job_id="job_01JC8YX4D2F7H1K9M3P5Q8S0TV", pose_model="fake", proposer=proposer
        )

    assert list(raw_output_validator.iter_errors(document)) == []
    assert all(frame["bar"] is None for frame in document["frames"])
