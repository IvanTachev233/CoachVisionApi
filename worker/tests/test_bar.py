import itertools
import math
import statistics

import numpy as np
import pytest
from conftest import fake_pose, squat_scenes

from vision_worker.bar import (
    BAR_DETECTOR,
    BarDetection,
    BarTracker,
    drop_islands,
    smooth_path,
)

WIDTH, HEIGHT = 720, 1280
RADIUS = 96.0
FRAMES = 60


def track(frames, poses):
    tracker = BarTracker(WIDTH, HEIGHT)
    for frame, pose in zip(frames, poses):
        tracker.add(frame, pose)
    return tracker.finish()


def poses_for(centres):
    return [fake_pose(centre, i) for i, centre in enumerate(centres)]


def test_detector_has_a_name_and_version():
    assert BAR_DETECTOR.startswith("opencv-")
    assert BAR_DETECTOR.rsplit("@", 1)[1].isdigit()


def test_follows_the_loaded_plate_and_ignores_the_one_on_the_rack():
    frames, centres = squat_scenes(WIDTH, HEIGHT, FRAMES, RADIUS)

    bars = track(frames, poses_for(centres))

    assert len(bars) == FRAMES
    found = [(bar, centre) for bar, centre in zip(bars, centres) if bar is not None]
    assert len(found) >= 0.9 * FRAMES
    errors = [max(abs(bar.x - cx), abs(bar.y - cy)) for bar, (cx, cy) in found]
    assert statistics.median(errors) <= 4.0
    assert max(errors) <= 12.0
    assert statistics.median(bar.radius_px for bar, _ in found) == pytest.approx(RADIUS, rel=0.08)
    assert all(0.0 <= bar.confidence <= 1.0 for bar, _ in found)
    assert statistics.median(bar.confidence for bar, _ in found) >= 0.6


def test_plate_seen_at_an_angle_reports_its_longer_radius():
    # From a diagonal the plate looks like an upright oval; its height still gives the scale.
    frames, centres = squat_scenes(WIDTH, HEIGHT, FRAMES, RADIUS, squash=0.72)

    bars = track(frames, poses_for(centres))

    found = [(bar, centre) for bar, centre in zip(bars, centres) if bar is not None]
    assert len(found) >= 0.8 * FRAMES
    assert statistics.median(bar.radius_px for bar, _ in found) == pytest.approx(RADIUS, rel=0.1)
    errors = [max(abs(bar.x - cx), abs(bar.y - cy)) for bar, (cx, cy) in found]
    assert statistics.median(errors) <= 5.0


def test_clip_without_a_loaded_plate_gives_no_positions():
    frames, centres = squat_scenes(WIDTH, HEIGHT, FRAMES, RADIUS, plate=False)

    bars = track(frames, poses_for(centres))

    assert bars == [None] * FRAMES


def test_clip_without_a_pose_gives_no_positions():
    frames, _ = squat_scenes(WIDTH, HEIGHT, FRAMES, RADIUS)

    bars = track(frames, [None] * FRAMES)

    assert bars == [None] * FRAMES


def test_frames_without_a_pose_are_skipped_not_guessed():
    frames, centres = squat_scenes(WIDTH, HEIGHT, FRAMES, RADIUS)
    poses = poses_for(centres)
    for i in range(20, 26):
        poses[i] = None

    bars = track(frames, poses)

    assert all(bars[i] is None for i in range(20, 26))
    assert sum(bar is not None for bar in bars) >= 0.8 * FRAMES


def test_no_frames_is_not_an_error():
    assert BarTracker(WIDTH, HEIGHT).finish() == []


def wobbly_track(count=40, wobble=3.0):
    """Detections along a smooth descent and ascent, each up to `wobble` pixels off in place and size."""
    true = [(300.0 + 0.2 * i, 400.0 + 0.5 * (i - 20) ** 2) for i in range(count)]
    noise = np.random.default_rng(5).uniform(-wobble, wobble, size=(count, 3))
    bars = [
        BarDetection(x=x + dx, y=y + dy, radius_px=90.0 + dr, confidence=0.5)
        for (x, y), (dx, dy, dr) in zip(true, noise)
    ]
    return bars, true


def test_a_wobbly_path_is_smoothed_onto_the_way_the_plate_went():
    bars, true = wobbly_track()

    smooth = smooth_path(bars)

    off = [math.dist((bar.x, bar.y), centre) for bar, centre in zip(smooth, true)]
    assert max(off[3:-3]) <= 2.5
    assert max(off) <= 3.0  # the ends have neighbours on one side only
    assert all(abs(bar.radius_px - 90.0) <= 2.5 for bar in smooth[3:-3])
    assert [bar.confidence for bar in smooth] == [0.5] * len(bars)


def test_smoothing_does_not_make_up_positions_or_reach_across_a_gap():
    bars, true = wobbly_track()
    bars[18:21] = [None] * 3
    bars[21] = BarDetection(x=900.0, y=900.0, radius_px=90.0, confidence=0.5)  # alone after the gap
    bars[22:25] = [None] * 3

    smooth = smooth_path(bars)

    assert [bar is None for bar in smooth] == [bar is None for bar in bars]
    assert smooth[21] == bars[21]
    assert max(math.dist((bar.x, bar.y), centre) for bar, centre in zip(smooth[3:15], true[3:15])) <= 2.5


def test_a_circle_that_jumps_aside_for_a_frame_or_two_does_not_bend_the_path():
    # The rim is sometimes measured a fifth of a radius off for one or two frames: a zigzag in the path.
    bars, true = wobbly_track(wobble=0.5)
    for i in (12, 25, 26):
        bars[i] = BarDetection(x=bars[i].x - 20.0, y=bars[i].y + 10.0, radius_px=105.0, confidence=0.5)

    smooth = smooth_path(bars)

    off = [math.dist((bar.x, bar.y), centre) for bar, centre in zip(smooth, true)]
    assert max(off[3:-3]) <= 2.0
    assert all(abs(bar.radius_px - 90.0) <= 2.0 for bar in smooth[3:-3])


def test_a_track_that_keeps_hopping_between_two_places_is_smoothed_onto_the_one_it_is_mostly_at():
    # Two frames aside, two back, two aside again: four odd frames within one stretch of the path.
    bars, true = wobbly_track(wobble=0.5)
    for i in (20, 21, 24, 25):
        bars[i] = BarDetection(x=bars[i].x - 17.0, y=bars[i].y + 20.0, radius_px=103.0, confidence=0.5)

    smooth = smooth_path(bars)

    off = [math.dist((bar.x, bar.y), centre) for bar, centre in zip(smooth, true)]
    assert max(off[5:-5]) <= 2.0
    heights = [bar.y for bar in smooth[22:34]]
    assert all(later > earlier for earlier, later in itertools.pairwise(heights))  # it only went down here


def test_a_few_frames_far_from_where_the_track_is_before_and_after_them_are_dropped():
    bars, _ = wobbly_track(wobble=0.5)
    bars[15:17] = [None] * 2
    for i in (17, 18):
        bars[i] = BarDetection(x=bars[i].x - 25.0, y=bars[i].y + 50.0, radius_px=90.0, confidence=0.5)
    bars[19:24] = [None] * 5

    kept = drop_islands(bars)

    assert kept[17] is None and kept[18] is None
    assert [bar is None for i, bar in enumerate(kept) if i not in (17, 18)] == [
        bar is None for i, bar in enumerate(bars) if i not in (17, 18)
    ]


def test_a_few_frames_between_gaps_that_lie_on_the_track_s_way_are_kept():
    bars, _ = wobbly_track(wobble=0.5)
    bars[15:17] = [None] * 2
    bars[19:24] = [None] * 5

    assert drop_islands(bars) == bars
