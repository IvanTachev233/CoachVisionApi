"""Smoothing what is measured frame by frame: the bar's track and the body's points.

Each frame is measured by itself, so a path drawn through the measurements wobbles, and now and
then one is well off for a frame or two. Here a series of rows (one per frame, of any numbers
that move together: x and y, or x, y and a size) is put on the way its rows go: first the rows
that jumped aside are told from the rest, then each row is replaced by the value of a parabola
fitted to the rows around it that did not jump.

Nothing here needs a model or a picture, so the overlay can use it without loading either.
"""

import itertools
import math

import numpy as np

SMOOTH_FRAMES = 7  # frames the curve is fitted to; a run of fewer frames is left as measured
JUMP_FRAMES = 11  # frames looked at together to tell which of them jumped aside
JUMP_ODD = 4  # of JUMP_FRAMES may have jumped aside and still be told from the rest

# Body points.
POSE_SURE = 0.5  # confidence from which a point holds its curve up, and is drawn in full
POSE_JUMP = 0.1  # of the torso: a point this close to the others' curve has not jumped
BRIDGE_FRAMES = 30  # the longest lapse (frames a point is unsure, between sure ones) that is bridged
# An arm hangs off the body, so across a lapse its joints go with the joint above them. A leg
# stands on the floor: its joints are bridged in the picture, straight from one place to the other.
CARRIED_BY = {
    "left_elbow": "left_shoulder",
    "right_elbow": "right_shoulder",
    "left_wrist": "left_elbow",
    "right_wrist": "right_elbow",
}
FEET = ("left_ankle", "right_ankle")
PLANTED_LIFTS = frozenset({"squat"})  # lifts whose feet stay where they are put (checked on squats only)
PLANTED_AREA = 0.06  # of the torso: a foot whose sure positions stay this close to one place is standing
PLANTED_FRAMES = 30  # sure frames a foot has to stand for to be held there


def on_a_curve(values, near, support=None):
    """The rows of `values` (at least SMOOTH_FRAMES of them, one per frame) put on the way they go.

    `near` is how far a row may lie from the others' curve, all its numbers taken together,
    without being called a jump. `support` says, per row, whether it may hold the curve up; by
    default every row may. A row that may not (a body point the model is unsure of) is still
    put on the curve when supporting rows stand before and after it among the frames the curve
    is fitted to; otherwise it stays as measured, as does any row with too few supporting rows
    around it to fit a curve.
    """
    values = np.asarray(values, dtype=np.float64)
    count = len(values)
    if support is None:
        holds = ~_jumped(values, near)
    else:
        support = np.asarray(support, dtype=bool)
        if support.sum() < 3:
            return values.copy()
        # To tell the jumps among the supporting rows, the others are first put on the straight
        # way between their supporting neighbours: as measured they would all look like jumps.
        at = np.arange(count)
        bridged = np.stack([np.interp(at, at[support], column[support]) for column in values.T], axis=1)
        holds = support & ~_jumped(bridged, near)
    curve, _, kept, steps = _parabolas(values, SMOOTH_FRAMES, np.broadcast_to(holds, (count, count)))
    if support is None:
        return curve
    fitted = kept.sum(axis=1) >= 3
    between = (kept * (steps < 0)).any(axis=1) & (kept * (steps > 0)).any(axis=1)
    return np.where((fitted & (support | between))[:, None], curve, values)


def _parabolas(values, width, use):
    """For each row of `values`, parabolas fitted to the `width` rows around it.

    `use[i, j]` says whether row j counts in row i's fit; a row with fewer than three rows left
    to fit uses all of them. Returns (the parabolas' values at each row, how far each of the
    `width` rows lies from them with all its numbers taken together, which of those rows were
    asked to count, how many frames each of them is from the row). At the ends the nearest full
    set of rows is used.
    """
    count = len(values)
    first = np.clip(np.arange(count) - width // 2, 0, count - width)
    rows = first[:, None] + np.arange(width)[None, :]
    steps = (rows - np.arange(count)[:, None]).astype(np.float64)  # frames from the row being fitted for
    seen = values[rows]  # count x width x numbers per row
    terms = np.stack([np.ones_like(steps), steps, steps**2], axis=2)
    asked = np.take_along_axis(use, rows, axis=1).astype(np.float64)
    kept = asked.copy()
    kept[kept.sum(axis=1) < 3] = 1.0
    weighted = (terms * kept[..., None]).transpose(0, 2, 1)
    curve = np.linalg.solve(weighted @ terms, weighted @ seen)
    off = np.sqrt(((seen - terms @ curve) ** 2).sum(axis=2))
    return curve[:, 0, :], off, asked, steps


def _jumped(values, near):
    """Which rows of `values` jumped aside from the way the rows around them go.

    Up to JUMP_ODD of the JUMP_FRAMES rows around a row may be odd ones, so parabolas are
    fitted with every choice of that many left out, and the choice that passes closest to the
    rows it kept is taken as the way the track goes there. A row has jumped if it lies far
    from that: further than `near`, and than three times how far the kept rows lie. A frame
    that jumped is off in all its numbers together, so rows are judged as wholes. A track that
    hops between two places for a while is told apart this way; a path that really turns, as
    the bar does at the bottom of a lift, is not taken for a jump as long as most of the rows
    around the turn lie on one parabola.
    """
    count = len(values)
    width = min(JUMP_FRAMES, count)
    own = np.arange(count) - np.clip(np.arange(count) - width // 2, 0, count - width)  # a row's place in its set
    best_off, best_spread = None, None
    for left_out in itertools.combinations(range(width), JUMP_ODD * width // JUMP_FRAMES):
        kept = np.ones(width, dtype=bool)
        kept[list(left_out)] = False
        use = np.zeros((count, count), dtype=bool)
        rows = np.clip(np.arange(count) - width // 2, 0, count - width)[:, None] + np.flatnonzero(kept)[None, :]
        np.put_along_axis(use, rows, True, axis=1)
        _, off, _, _ = _parabolas(values, width, use)
        spread = np.sqrt((off[:, kept] ** 2).mean(axis=1))
        if best_off is None:
            best_off, best_spread = off, spread
        else:
            better = spread < best_spread
            best_off, best_spread = np.where(better[:, None], off, best_off), np.where(better, spread, best_spread)
    # Where even the kept rows don't lie on a parabola, the path turns too fast over these frames
    # for this to tell a jump from the turn, and nothing is called a jump.
    on_one_curve = best_spread <= near / 3
    # Nor at a run's ends, where a row has neighbours on one side only: a path that stops or
    # starts there looks like a jump from that side.
    in_the_middle = own == width // 2
    return in_the_middle & on_one_curve & (best_off[np.arange(count), own] > np.maximum(3 * best_spread, near))


def smooth_pose(poses, *, feet_planted=False):
    """`poses` (one dict of named points, or None, per frame) with each point smoothed over time.

    A joint moves, so nothing is held in place: each point is put on the way it goes over the
    frames around it. Points the model is unsure of (confidence under POSE_SURE: a wrist behind
    a plate, say) wobble several times more than the others, so they don't hold the curve up;
    between sure frames they are put on it. A bone's length is not forced either: in the
    picture it really changes, as a thigh pointing at the camera is shorter than one seen from
    the side.

    Where a point is unsure for up to BRIDGE_FRAMES frames between sure ones, the model's
    guesses are dropped for a bridge from the one sure place to the other (`_bridge`). With
    `feet_planted` (a squat, not a snatch) an ankle is held at one place for as long as its
    sure positions stay there (`_stands`).

    Confidences are left as the model gave them, no point is added or taken away, and nothing
    is carried across frames that don't report a point. Without shoulders and hips to measure
    a jump by, the poses come back as they were.
    """
    torsos = [
        math.dist(_middle(pose, "left_shoulder", "right_shoulder"), _middle(pose, "left_hip", "right_hip"))
        for pose in poses
        if pose and all(name in pose for name in ("left_shoulder", "right_shoulder", "left_hip", "right_hip"))
    ]
    if not torsos:
        return list(poses)
    torso = float(np.percentile(torsos, 90))
    near = POSE_JUMP * torso
    out = [None if pose is None else {name: dict(point) for name, point in pose.items()} for pose in poses]
    for name in sorted({name for pose in poses if pose for name in pose}):
        start = None
        for i in range(len(poses) + 1):
            there = i < len(poses) and poses[i] is not None and name in poses[i]
            if there and start is None:
                start = i
            elif not there and start is not None:
                if i - start >= SMOOTH_FRAMES:
                    run = [poses[k][name] for k in range(start, i)]
                    seen = np.array([(point["x"], point["y"]) for point in run])
                    sure = np.array([point["confidence"] >= POSE_SURE for point in run])
                    curve = on_a_curve(seen, near, support=sure)
                    curve = _steadied_guesses(seen, curve, sure, near)
                    for k, (x, y) in zip(range(start, i), curve):
                        out[k][name]["x"], out[k][name]["y"] = float(x), float(y)
                start = None
    # A joint above is bridged before the one it carries, so that one can go by it.
    placed = {}
    for name in sorted({name for pose in poses if pose for name in pose}, key=lambda name: (name in CARRIED_BY, "wrist" in name)):
        placed[name] = _bridge(poses, out, name, placed.get(CARRIED_BY.get(name)))
    if feet_planted:
        for name in FEET:
            for first, last, place in _stands(poses, out, name, PLANTED_AREA * torso):
                for k in range(first, last + 1):
                    if poses[k] is not None and name in poses[k]:
                        out[k][name]["x"], out[k][name]["y"] = place
    return out


def _bridge(poses, out, name, carrier_placed):
    """Bridge the lapses of the point `name` in `out`; returns, per frame, whether it is now placed.

    A lapse is a stretch of frames that report the point without being sure of it, with a sure
    frame right before and right after. One of up to BRIDGE_FRAMES frames is bridged: the
    point's place (in the picture, or from the joint that carries it, where that one is placed
    all the way) goes evenly from what it is in the one sure frame to what it is in the other.
    Shorter lapses than the curve already covers are left to it.
    """
    count = len(poses)
    there = np.array([pose is not None and name in pose for pose in poses])
    sure = np.array([bool(t) and poses[k][name]["confidence"] >= POSE_SURE for k, t in enumerate(there)])
    placed = sure.copy()
    carrier = CARRIED_BY.get(name)
    i = 1
    while i < count:
        if not (there[i] and not sure[i] and sure[i - 1]):
            i += 1
            continue
        j = i
        while j < count and there[j] and not sure[j]:
            j += 1
        length = j - i
        if j < count and sure[j] and SMOOTH_FRAMES - 3 < length <= BRIDGE_FRAMES:
            carried = carrier is not None and carrier_placed is not None and bool(carrier_placed[i - 1 : j + 1].all())
            if carried or carrier is None:
                # Each frame's place of the carrier, or the picture's corner for a point nothing carries.
                base = np.array([(out[k][carrier]["x"], out[k][carrier]["y"]) if carried else (0.0, 0.0) for k in range(i - 1, j + 1)])
                own = np.array([(out[k][name]["x"], out[k][name]["y"]) for k in (i - 1, j)])
                share = (np.arange(1, length + 1) / (length + 1))[:, None]
                bridge = base[1:-1] + (own[0] - base[0]) * (1 - share) + (own[1] - base[-1]) * share
                for k, (x, y) in zip(range(i, j), bridge):
                    out[k][name]["x"], out[k][name]["y"] = float(x), float(y)
                placed[i:j] = True
        i = j
    return placed


def _stands(poses, out, name, area):
    """Where the foot point `name` stands: (first frame, last frame, (x, y)) per stretch.

    Only frames sure of the point are gone by. A frame stands if nearly all the sure frames
    around it lie within `area` of their middle; a stretch of PLANTED_FRAMES or more such
    frames on end is a foot standing, at the middle of their positions. A step in between
    (a walk-out before a squat) breaks the stretch, so each place the foot stands gets its own.
    """
    at = [k for k, pose in enumerate(poses) if pose is not None and name in pose and pose[name]["confidence"] >= POSE_SURE]
    if len(at) < PLANTED_FRAMES:
        return []
    seen = np.array([(out[k][name]["x"], out[k][name]["y"]) for k in at])
    half = PLANTED_FRAMES // 2
    stands = np.zeros(len(at), dtype=bool)
    for k in range(len(at)):
        around = seen[max(k - half, 0) : k + half + 1]
        stands[k] = (np.hypot(*(around - np.median(around, axis=0)).T) <= area).mean() >= 0.9
    stretches = []
    start = None
    for k in range(len(at) + 1):
        if k < len(at) and stands[k]:
            start = k if start is None else start
        elif start is not None:
            if k - start >= PLANTED_FRAMES:
                x, y = np.median(seen[start:k], axis=0)
                stretches.append((at[start], at[k - 1], (float(x), float(y))))
            start = None
    return stretches


def _steadied_guesses(seen, curve, sure, near):
    """`curve` with the long stretches of unsure rows smoothed among themselves.

    Where the model is unsure for many frames on end there is no sure frame near to go by, and
    `on_a_curve` leaves those rows as measured. They are still the model's best guess, and a
    guess that dances from frame to frame is no better for it: each such stretch is put on its
    own curve. The rows stay marked as unsure by their confidence.
    """
    out = curve.copy()
    untouched = ~sure & (curve == seen).all(axis=1)
    start = None
    for i in range(len(seen) + 1):
        if i < len(seen) and untouched[i]:
            start = i if start is None else start
        elif start is not None:
            if i - start >= SMOOTH_FRAMES:
                out[start:i] = on_a_curve(seen[start:i], near)
            start = None
    return out


def _middle(pose, left, right):
    return ((pose[left]["x"] + pose[right]["x"]) / 2, (pose[left]["y"] + pose[right]["y"]) / 2)
