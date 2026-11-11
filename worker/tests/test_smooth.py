"""Smoothing a series of measured points over time: the curve itself, and body points with their confidences."""

import math

import numpy as np

from vision_worker.smooth import POSE_SURE, SMOOTH_FRAMES, on_a_curve, smooth_pose

TORSO = 150.0  # pixels from shoulders to hips in the made-up poses below


def path(count=40):
    """A point going down and up again, as a joint does in a lift: (x, y) per frame."""
    return np.array([(300.0 + 0.2 * i, 400.0 + 0.5 * (i - 20) ** 2) for i in range(count)])


def wobbly(count=40, wobble=3.0, seed=5):
    true = path(count)
    return true + np.random.default_rng(seed).uniform(-wobble, wobble, size=true.shape), true


def off(rows, true):
    return np.hypot(*(np.asarray(rows) - true).T)


def test_wobbly_rows_are_put_close_to_the_way_they_go():
    seen, true = wobbly()

    smooth = on_a_curve(seen, near=10.0)

    # Rows up to 3 off each way: a 7-frame curve takes about half of that out, not all of it.
    assert off(smooth, true)[3:-3].max() <= 3.5
    assert off(smooth, true)[3:-3].mean() < 0.7 * off(seen, true)[3:-3].mean()


def test_rows_that_may_not_hold_the_curve_up_do_not_pull_it():
    seen, true = wobbly(wobble=0.5)
    support = np.ones(len(seen), dtype=bool)
    for i in (10, 11, 20, 26, 27, 28):
        seen[i] += (40.0, -30.0)
        support[i] = False

    smooth = on_a_curve(seen, near=10.0, support=support)

    assert off(smooth, true)[support][3:-3].max() <= 2.0


def test_an_unsupported_row_between_supported_ones_is_put_on_their_curve():
    seen, true = wobbly(wobble=0.5)
    support = np.ones(len(seen), dtype=bool)
    seen[15] += (40.0, -30.0)
    support[15] = False

    smooth = on_a_curve(seen, near=10.0, support=support)

    assert off(smooth, true)[15] <= 2.0


def test_unsupported_rows_with_no_supported_ones_near_stay_as_measured():
    seen, _ = wobbly(wobble=0.5)
    support = np.ones(len(seen), dtype=bool)
    support[12:30] = False  # longer than the curve is fitted over
    seen[12:30] += (40.0, -30.0)

    smooth = on_a_curve(seen, near=10.0, support=support)

    assert np.array_equal(smooth[18:24], seen[18:24])


def pose_at(point, confidence=0.9, shoulder=(300.0, 200.0)):
    """A pose with one moving joint (a knee) and still shoulders and hips, TORSO apart."""
    sx, sy = shoulder
    fixed = {"left_shoulder": (sx, sy), "right_shoulder": (sx, sy), "left_hip": (sx, sy + TORSO), "right_hip": (sx, sy + TORSO)}
    pose = {name: {"x": x, "y": y, "confidence": 0.95} for name, (x, y) in fixed.items()}
    if point is not None:
        pose["left_knee"] = {"x": float(point[0]), "y": float(point[1]), "confidence": confidence}
    return pose


def knee(poses):
    return [None if pose is None or "left_knee" not in pose else (pose["left_knee"]["x"], pose["left_knee"]["y"]) for pose in poses]


def test_a_wobbly_joint_ends_up_close_to_the_way_it_goes():
    seen, true = wobbly()
    poses = [pose_at(point) for point in seen]

    smooth = smooth_pose(poses)

    assert off(knee(smooth), true)[3:-3].max() <= 3.5
    assert off(knee(smooth), true)[3:-3].mean() < 0.7 * off(seen, true)[3:-3].mean()
    assert poses[10]["left_knee"]["x"] == seen[10][0]  # what was given is left as it was


def test_a_joint_that_jumps_aside_for_a_frame_or_two_does_not_bend_its_path():
    seen, true = wobbly(wobble=0.5)
    for i in (12, 25, 26):
        seen[i] += (-30.0, 25.0)  # 0.26 of the torso

    smooth = smooth_pose([pose_at(point) for point in seen])

    assert off(knee(smooth), true)[5:-5].max() <= 2.0


def test_frames_the_model_is_unsure_of_do_not_pull_the_curve_and_land_on_it_between_sure_ones():
    seen, true = wobbly(wobble=0.5)
    unsure = (10, 11, 20, 27)
    for i in unsure:
        seen[i] += (40.0, -30.0)
    poses = [pose_at(point, confidence=0.2 if i in unsure else 0.9) for i, point in enumerate(seen)]

    smooth = smooth_pose(poses)

    assert off(knee(smooth), true)[5:-5].max() <= 2.0


def test_nothing_is_made_up_and_confidences_are_left_alone():
    seen, _ = wobbly()
    poses = [pose_at(point, confidence=0.3 + 0.01 * i) for i, point in enumerate(seen)]
    poses[8] = None  # no body found
    poses[20] = pose_at(None)  # a body, but no knee reported
    poses[21] = pose_at(None)

    smooth = smooth_pose(poses)

    assert smooth[8] is None
    assert "left_knee" not in smooth[20] and "left_knee" not in smooth[21]
    assert [set(pose) for pose in smooth if pose] == [set(pose) for pose in poses if pose]
    assert [pose["left_knee"]["confidence"] for pose in smooth if pose and "left_knee" in pose] == [
        pose["left_knee"]["confidence"] for pose in poses if pose and "left_knee" in pose
    ]


def test_a_run_too_short_to_fit_a_curve_to_is_left_as_measured():
    seen, _ = wobbly(count=SMOOTH_FRAMES - 1)
    poses = [pose_at(point) for point in seen]

    assert smooth_pose(poses) == poses


def test_poses_without_a_torso_to_measure_by_are_left_as_measured():
    poses = [{"left_knee": {"x": 10.0 + i, "y": 20.0, "confidence": 0.9}} for i in range(20)]

    assert smooth_pose(poses) == poses
    assert smooth_pose([]) == []
    assert smooth_pose([None, None]) == [None, None]


def test_sure_means_at_least_half():
    assert math.isclose(POSE_SURE, 0.5)


def test_a_long_stretch_the_model_is_unsure_of_is_steadied_among_itself():
    # A wrist behind a plate for most of a lift: no sure frame near, but the guesses still
    # shouldn't dance. They are smoothed by each other and stay marked as unsure.
    seen, true = wobbly(wobble=6.0, seed=9)
    poses = [pose_at(point, confidence=0.9 if i < 8 else 0.2) for i, point in enumerate(seen)]

    smooth = smooth_pose(poses)

    assert off(knee(smooth), true)[12:-3].mean() < 0.7 * off(seen, true)[12:-3].mean()
    assert all(pose["left_knee"]["confidence"] == 0.2 for pose in smooth[8:])


def lifter(points, confidences=None, shoulders=None):
    """Poses with a torso TORSO long and the named joints at `points[name][frame]`."""
    count = len(next(iter(points.values())))
    poses = []
    for i in range(count):
        sx, sy = shoulders[i] if shoulders is not None else (300.0, 200.0)
        fixed = {"left_shoulder": (sx, sy), "right_shoulder": (sx, sy), "left_hip": (sx, sy + TORSO), "right_hip": (sx, sy + TORSO)}
        pose = {name: {"x": float(x), "y": float(y), "confidence": 0.95} for name, (x, y) in fixed.items()}
        for name, path in points.items():
            confidence = 0.9 if confidences is None else confidences[name][i]
            pose[name] = {"x": float(path[i][0]), "y": float(path[i][1]), "confidence": confidence}
        poses.append(pose)
    return poses


def places(poses, name):
    return np.array([(pose[name]["x"], pose[name]["y"]) for pose in poses])


def straight(count=60):
    return np.array([(300.0 + 2.0 * i, 500.0 + 1.0 * i) for i in range(count)])


def test_a_lapse_between_sure_frames_is_bridged_from_one_to_the_other():
    # A knee the model loses for two thirds of a second: its guesses there are well off, and
    # steady enough to look right. The knee is put on the way from where it was last sure to
    # where it is sure again.
    true = straight()
    seen = true.copy()
    seen[20:40] += (45.0, -35.0)
    confidences = {"left_knee": [0.2 if 20 <= i < 40 else 0.9 for i in range(len(true))]}

    smooth = smooth_pose(lifter({"left_knee": seen}, confidences))

    assert off(places(smooth, "left_knee"), true).max() <= 2.0
    assert [pose["left_knee"]["confidence"] for pose in smooth] == confidences["left_knee"]


def test_a_lapse_too_long_to_bridge_keeps_the_models_guesses():
    true = straight(90)
    seen = true.copy()
    seen[20:70] += (45.0, -35.0)
    confidences = {"left_knee": [0.2 if 20 <= i < 70 else 0.9 for i in range(len(true))]}

    smooth = smooth_pose(lifter({"left_knee": seen}, confidences))

    assert off(places(smooth, "left_knee"), true)[30:60].min() >= 40.0


def test_an_arm_joint_is_bridged_by_its_place_from_the_joint_above():
    # The shoulders go down and up while the elbow is lost; the elbow goes with them, not
    # straight across from where it was to where it is found again.
    count = 60
    shoulders = np.array([(300.0, 200.0 + 60.0 * math.sin(math.pi * i / (count - 1))) for i in range(count)])
    true = shoulders + (40.0, 90.0)
    seen = true.copy()
    seen[20:40] += (-50.0, 30.0)
    confidences = {"left_elbow": [0.2 if 20 <= i < 40 else 0.9 for i in range(count)]}

    smooth = smooth_pose(lifter({"left_elbow": seen}, confidences, shoulders))

    assert off(places(smooth, "left_elbow"), true)[3:-3].max() <= 3.0


def planted_foot(count=80, seed=3):
    true = np.tile((320.0, 900.0), (count, 1))
    return true + np.random.default_rng(seed).uniform(-3.0, 3.0, size=true.shape), true


def test_feet_are_held_in_place_when_the_lift_keeps_them_planted():
    seen, true = planted_foot()
    seen[30:36] += (30.0, -20.0)  # behind a plate for a moment, guessed
    confidences = {"left_ankle": [0.2 if 30 <= i < 36 else 0.9 for i in range(len(seen))]}
    poses = lifter({"left_ankle": seen}, confidences)

    held = places(smooth_pose(poses, feet_planted=True), "left_ankle")
    free = places(smooth_pose(poses), "left_ankle")

    assert np.ptp(held, axis=0).max() == 0.0  # the same place in every frame
    assert off(held, true).max() <= 1.0
    assert np.ptp(free, axis=0).max() > 0.5  # without the setting the foot is only smoothed


def test_a_foot_that_steps_is_held_only_where_it_stands():
    # A walk-out: the foot stands, steps a third of the torso to the side, and stands again.
    stand, step = 50, 20
    xs = np.concatenate([np.full(stand, 300.0), np.linspace(300.0, 350.0, step + 2)[1:-1], np.full(stand, 350.0)])
    true = np.stack([xs, np.full(len(xs), 900.0)], axis=1)
    seen = true + np.random.default_rng(4).uniform(-2.0, 2.0, size=true.shape)

    held = places(smooth_pose(lifter({"left_ankle": seen}), feet_planted=True), "left_ankle")

    assert np.ptp(held[:40], axis=0).max() == 0.0 and abs(held[0][0] - 300.0) <= 1.0
    assert np.ptp(held[-40:], axis=0).max() == 0.0 and abs(held[-1][0] - 350.0) <= 1.0
    assert abs(held[stand + step // 2][0] - 325.0) <= 6.0  # on its way, not held at either end
