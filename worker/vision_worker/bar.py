"""Finds the loaded plate at the end of the bar and follows it through a clip.

Two ways in, both ending in one position per frame or none:

- `BarTracker.finish()` finds the plate by itself. It needs the lifter's shoulders, so it is
  tied to lifts where the bar stays put relative to them (the squat). Described below.
- `BarTracker.follow()` starts from a mark, the plate's centre and a point on its rim in one
  frame, and follows that plate forwards and backwards from there. It uses no pose, so it is
  not tied to any lift. Described at "Following a marked plate". Two options, each on unless
  turned off, add to how it looks for the plate: by its colour, and by a part of its rim.
- `BarTracker.find()` finds the plate from a proposer's boxes (a detector model's guesses at
  where round things are) and follows it like a marked one. Described at "Finding the plate
  from proposals". The loaded plate is told from other round things by its own movement, not
  by the lifter's, so it is not tied to a lift; the pose only gives the lifter's size.

Without a mark the plate is found by shape, not colour, in three steps:

1. In a squat the bar rides on the back, so the loaded plate keeps its place
   relative to the lifter's shoulders while they move. A sample of frames is
   lined up on the shoulders and their median taken: what travels with the
   lifter stays sharp, and the background, plates on a rack included, smears
   away.
2. Circles and upright ovals are proposed in that one image (Hough transform,
   also on sideways-stretched copies, since a plate seen from a diagonal is an
   oval). The head is left out. Each proposal is then tried on real frames,
   and the one whose rim is found most often is the plate.
3. The rim is measured in every frame: rays from the expected centre look for
   the rim's edge, and the centre and radius that fit those edge points are
   the result. The longer radius is reported, since that one gives the scale.

Needs the pose: a frame without shoulders gets no bar position.
"""

import itertools
import math
from dataclasses import dataclass, replace

import cv2
import numpy as np

from vision_worker.smooth import SMOOTH_FRAMES, on_a_curve

BAR_DETECTOR = "opencv-hough-plates@1"  # the plate found by itself
MARKED_DETECTOR = "opencv-marked-plate@1"  # the plate followed from a mark

# Detection runs on a grey copy with this long side; results are scaled back to the video's pixels.
WORK_LONG_SIDE = 640
JPEG_QUALITY = 90

# Step 1: the picture of what travels with the shoulders.
ALIGN_FRAMES = 40
ALIGN_HALF_WIDTH = 3.2  # of the torso: the bar's end can be far to the side in a diagonal view
ALIGN_HALF_HEIGHT = 1.7
MIN_TRAVEL_PER_TORSO = 0.4  # the shoulders must move this much, or the background can't be told apart

# Step 2: proposals and their trial.
STRETCHES = (1.0, 0.9, 0.8, 0.7, 0.6, 0.5)  # width over height of the ovals looked for
HOUGH_DP = 2
HOUGH_EDGE = 100
HOUGH_VOTES = 20
CIRCLES_PER_STRETCH = 40
MAX_PROPOSALS = 6
TRIAL_FRAMES = 60
MIN_TRIAL_FOUND = 0.3  # share of the trial frames in which the rim must be found
# A 450 mm plate is about 0.45 of a shoulder-to-hip length; a head is about 0.3 with its hair.
MIN_RADIUS_PER_TORSO = 0.3
MAX_RADIUS_PER_TORSO = 1.1
HEAD_REACH = 0.6  # of the radius: a proposal centred this close to the nose is the head

# Step 3: the rim.
ANGLES = 90
EDGE_FLOOR = 6.0  # grey levels: weaker than this is not an edge
SEARCH_BANDS = (0.25, 0.1)  # of the radius: first look, then a closer one from the corrected centre
RIM_TOLERANCE = 0.04  # of the radius: how far an edge point may sit from the fitted rim
MIN_RIM_SHARE = 0.4  # of the rays that must find the rim
MIN_OCTANTS = 5  # of 8: the rim must be seen around the plate, not on one side
MIN_FRAMES = 6
MAX_SCALE_CHANGE = 0.15
MAX_DRIFT = 0.8  # of the radius: how far the plate may sit from its usual place by the shoulders
KEEP_OFFSET_FRAMES = 15

# Following a marked plate.
# From a click the first fit starts further off than from a measured rim, so fewer rays agree at first.
EARLY_SHARE = 0.2
MIN_MARK_RADIUS = 6.0  # working pixels: smaller than this, the rim can't be measured
MAX_MARK_SCALE = 0.25  # how far the measured rim may be from the marked size
MAX_MARK_OFF = 0.3  # of the radius: how far the measured centre may be from the marked one
MAX_STEP_SCALE = 0.1  # how far a frame's plate may be from the size it had over the last found frames
SIZE_MEMORY = 15  # found frames whose middle size is "the size it had": one odd rim can't start a drift
MAX_TOTAL_SCALE = 1.6  # and over the whole clip, either way, against the marked frame
PATCH_MARGIN = 1.15  # of the radii: the picture of the plate kept for finding it again
MIN_MATCH = 0.6  # how alike a place must look to that picture, 0 to 1
REACH_PER_MISSED_FRAME = 0.5  # of the radius: how much further to look for each frame it was lost
MAX_REACH = 4.0

# Smoothing a track (`smooth.on_a_curve`) and mending it.
JUMP = 0.12  # of the radius, place and size taken together: a frame this close to the others' curve has not jumped
ISLAND_FRAMES = 5  # a run of at most this many found frames, with none before and after it, is looked at
ISLAND_OFF = 0.3  # of the radius: further than this from the way between its neighbours, it is dropped

# The plate's middle: the hub and what is printed around it, kept as a picture from the marked frame.
CORE = 0.35  # of the radii: half the side of that picture
MIN_CORE_CONTRAST = 8.0  # grey levels: flatter than this, there is nothing in it to find
MIN_CORE_MATCH = 0.7  # how alike a place must look to it, 0 to 1
KEEP_CORE_MATCH = 0.5  # how alike it must look where the middle was on the plate in the frame before
CORE_STEP = 0.2  # of the radius: and how far from the expected place it may then be
CORE_REACH = 0.5  # of the radius: how far from the rim's centre the middle is looked for
MAX_CORE_REACH = 2.0  # and at most this far when the rim has been lost for some frames
SAME_CORE_SIZE = 0.03  # the picture is used as it is for a plate this close to the size it was cut at

# Option "colour": the plate's colour is taken from the mark and the rim looked for in a picture of that colour.
COLOUR_SAMPLE = (0.25, 0.45)  # of the marked radius: the ring sampled, clear of the hub and inside the narrowest oval
COLOUR_NEAR = (1.15, 1.6)  # of the marked radius: the ring just outside the plate, which must look different
COLOUR_REACH = 28.0  # how far from the plate's colour a pixel may be and still count as it (8-bit Lab units)
LIGHTNESS_WEIGHT = 0.4  # lightness counts for less than hue: light and shadow change it over a lift
MIN_COLOUR_ON_PLATE = 0.5  # share of the sampled ring that must have the plate's colour
MAX_COLOUR_AROUND = 0.35  # share of the ring outside that may have it, or the colour doesn't set the plate apart

# Option "partial rim": while following, a piece of the rim is enough, since the size is known by then.
ARC_STRETCH = 30  # of ANGLES: a third of the way around the plate
ARC_RAYS = 18  # of that stretch: the rays on which the rim must be the strongest edge
ARC_SHIFT = 0.3  # of the radius: how far from the expected place the centre is looked for

# Finding the plate from a proposer's boxes (VS2f, variant F).
FIND_FRAMES = 3  # frames shown to the proposer, spread evenly over the clip
MIN_BOX_SCORE = 0.15  # the proposer has no class for a plate, so its scores for one are low
MIN_BOX_HEIGHT = 12.0  # working pixels
BOX_SHAPES = (0.45, 1.25)  # width over height: a circle, or an upright oval
FIND_RADIUS_PER_TORSO = (0.22, 0.8)  # a plate's radius against the shoulder-to-hip length
FIND_FOLLOWS = 4  # boxes followed through the clip, best rim first
MIN_FOUND = 0.3  # share of the frames a track must have a position in
MIN_TRAVEL = 1.0  # of the radius: how far up and down a track must go to be the plate that is being lifted
MAX_SIZE_SPREAD = 1.4  # a track's larger sizes over its smaller ones: a plate keeps its size through a lift
MAX_FROM_LIFTER = 2.0  # of the torso: how far the plate may be from the nearest body point; the bar is in their hands
MIN_SHARED_FRAMES = 10  # frames in which two tracks both have a position, or they can't be compared
SAME_PLACE = 0.3  # of the radius: two circles this close are about one centre
SAME_SIZE = 0.2
MIN_GAP = 3  # frames without a position, between frames with one, that are worth following again for
FILL_FROM = (0, 3, 7)  # frames from the gap's edge at which following again is started, nearest first
FILL_FOLLOWS = 4  # tracks made this way for one clip, at most

_PHI = np.arange(ANGLES) * (2 * math.pi / ANGLES)
_COS = np.cos(_PHI).astype(np.float32)
_SIN = np.sin(_PHI).astype(np.float32)
_OCTANT = (np.arange(ANGLES) * 8) // ANGLES


@dataclass(frozen=True)
class BarDetection:
    """The plate in one frame, in the video's pixels."""

    x: float
    y: float
    radius_px: float
    confidence: float  # share of the rim that was seen, 0 to 1


def marked_detector(colour=False, partial_rim=False):
    """The name a raw output carries for a marked run with these options."""
    return MARKED_DETECTOR + ("+colour" if colour else "") + ("+partial-rim" if partial_rim else "")


def proposed_detector(proposer):
    """The name a raw output carries when the plate was found among `proposer`'s boxes."""
    return f"{proposer.name}+opencv-follow@1"


class MarkError(ValueError):
    """A mark that can't be used: outside the clip, or its two points are the same."""


@dataclass(frozen=True)
class Mark:
    """Where a person says the plate is, in the video's pixels: its centre and a point on its rim, at time `t`."""

    t: float  # seconds from the start of the video
    centre: tuple
    rim: tuple


def _runs(bars):
    """(start, end) of every run of frames with a position, `end` being the first frame after it."""
    runs, start = [], None
    for i, bar in enumerate([*bars, None]):
        if bar is not None and start is None:
            start = i
        elif bar is None and start is not None:
            runs.append((start, i))
            start = None
    return runs


def drop_islands(bars):
    """`bars` without the short runs that lie off the track's way.

    A follower that has lost the plate sometimes takes something else for it for a few frames
    before losing that too. Such a run stands alone between frames without a position; it is
    dropped when it lies far from the straight way between the runs before and after it.
    """
    out = list(bars)
    runs = _runs(bars)
    for (_, before), (start, end), (after, _) in zip(runs, runs[1:], runs[2:]):
        if end - start > ISLAND_FRAMES:
            continue
        a, b = bars[before - 1], bars[after]
        off = []
        for i in range(start, end):
            along = (i - (before - 1)) / (after - (before - 1))
            way = (a.x + along * (b.x - a.x), a.y + along * (b.y - a.y))
            off.append(math.dist((bars[i].x, bars[i].y), way) / bars[i].radius_px)
        if float(np.median(off)) > ISLAND_OFF:
            out[start:end] = [None] * (end - start)
    return out


def smooth_path(bars):
    """`bars` with each run of found frames smoothed: centres and sizes, not the share of the rim seen.

    Each frame's rim is measured by itself, to about a pixel of the working picture, and now
    and then well off for a frame or two, so a path drawn through the centres wobbles and
    zigzags and a speed taken from it is noisy, or has the wrong sign. First the frames that
    jumped aside are told from the rest, then each frame is put on a parabola fitted to the
    frames around it that did not jump. Frames without a position stay without one, and
    nothing is carried across them.
    """
    out = list(bars)
    for start, end in _runs(bars):
        if end - start < SMOOTH_FRAMES:
            continue
        run = np.array([(bar.x, bar.y, bar.radius_px) for bar in bars[start:end]])
        curve = on_a_curve(run, JUMP * float(np.median(run[:, 2])))
        out[start:end] = [
            replace(bar, x=float(x), y=float(y), radius_px=float(radius))
            for bar, (x, y, radius) in zip(bars[start:end], curve)
        ]
    return out


def _midpoint(pose, left, right):
    if pose is None or left not in pose or right not in pose:
        return None
    return ((pose[left]["x"] + pose[right]["x"]) / 2, (pose[left]["y"] + pose[right]["y"]) / 2)


def _polar_edges(gray, centre, max_radius):
    """Edge strength along rays from `centre`: one row per angle, one column per `step` pixels of radius."""
    bins = max(round(max_radius), 8)
    step = max_radius / bins
    polar = cv2.warpPolar(gray, (bins, ANGLES), centre, max_radius, cv2.INTER_LINEAR | cv2.WARP_POLAR_LINEAR)
    polar = cv2.GaussianBlur(polar.astype(np.float32), (5, 1), 0)
    edges = np.zeros_like(polar)
    edges[:, 1:-1] = np.abs(polar[:, 2:] - polar[:, :-2])

    # Past the frame's border there is no picture, and the border itself must not read as an edge.
    radius = np.arange(bins, dtype=np.float32) * step
    xs = centre[0] + radius[None, :] * _COS[:, None]
    ys = centre[1] + radius[None, :] * _SIN[:, None]
    height, width = gray.shape
    inside = (xs >= 1) & (xs <= width - 2) & (ys >= 1) & (ys <= height - 2)
    if bins > 6:
        clear = np.zeros_like(inside)
        clear[:, 3:-3] = inside[:, :-6] & inside[:, 6:]
        edges[~clear] = 0.0
    return edges, step


def _fit_rim(gray, centre, rx, ry, early_share=MIN_RIM_SHARE):
    """Measure the plate's rim near `centre`. Returns (x, y, scale, share of rays on the rim) or None.

    `rx` and `ry` are the plate's horizontal and vertical radii. Each ray takes its strongest edge
    near the expected rim; for a centre that is off by (ex, ey) and a size off by `scale`, a ray at
    angle phi meets the rim at about scale * rim(phi) + ex * cos(phi) + ey * sin(phi), which is
    linear in the three unknowns. Edge points that don't fit are dropped and the fit repeated.

    `early_share` is the share of rays that must agree while the fit is still being corrected;
    the result always needs MIN_RIM_SHARE.
    """
    rim = (rx * ry / np.sqrt((ry * _COS) ** 2 + (rx * _SIN) ** 2)).astype(np.float32)
    design = np.stack([rim, _COS, _SIN], axis=1)
    radius = max(rx, ry)
    tolerance = max(1.5, RIM_TOLERANCE * radius)
    x, y = centre
    scale = 1.0
    kept = None
    for band in SEARCH_BANDS:
        edges, step = _polar_edges(gray, (x, y), radius * scale * (1 + band) + 4)
        at = np.arange(edges.shape[1], dtype=np.float32)[None, :] * step
        expected = (rim * scale)[:, None]
        window = (at >= expected * (1 - band)) & (at <= expected * (1 + band))
        masked = np.where(window, edges, -1.0)
        peak = masked.argmax(axis=1)
        strong = masked[np.arange(ANGLES), peak] >= EDGE_FLOOR
        found = peak * step

        kept = strong
        solution = None
        for _ in range(3):
            if kept.sum() < early_share * ANGLES:
                return None
            solution = np.linalg.lstsq(design[kept], found[kept], rcond=None)[0]
            kept = strong & (np.abs(design @ solution - found) <= tolerance)
        scale, ex, ey = (float(v) for v in solution)
        x, y = x + ex, y + ey

    if kept.sum() < MIN_RIM_SHARE * ANGLES or len(set(_OCTANT[kept].tolist())) < MIN_OCTANTS:
        return None
    return x, y, scale, float(kept.sum()) / ANGLES


def _fit_checked(gray, centre, rx, ry):
    """`_fit_rim` for a rough starting place, with a check on the result. Returns the same, or None.

    From a click the fit starts further off than from a measured rim, so it is let through its
    first corrections on few rays (EARLY_SHARE). In return the finished rim is looked at once
    more, over the wide band: on MIN_RIM_SHARE of the rays, spread around the plate, the
    strongest edge must be the rim itself. Texture gives a fit a rim's worth of edge points only
    close up; over the wide band its strongest edges are somewhere else. The share reported is
    the one from this look.
    """
    fit = _fit_rim(gray, centre, rx, ry, EARLY_SHARE)
    if fit is None:
        return None
    x, y, scale, _ = fit
    on_rim = _strongest_edge_on_rim(gray, (x, y), rx * scale, ry * scale)[0]
    if on_rim.sum() < MIN_RIM_SHARE * ANGLES or len(set(_OCTANT[on_rim].tolist())) < MIN_OCTANTS:
        return None
    return x, y, scale, float(on_rim.sum()) / ANGLES


def _strongest_edge_on_rim(gray, centre, rx, ry, band=SEARCH_BANDS[0]):
    """Per ray: is the strongest edge within `band` of the rim the rim itself? Also where that edge is, and if it is one.

    Returns (on the rim, distance of the strongest edge from the centre, strong enough to be an edge), one entry per ray.
    """
    rim = (rx * ry / np.sqrt((ry * _COS) ** 2 + (rx * _SIN) ** 2)).astype(np.float32)
    radius = max(rx, ry)
    edges, step = _polar_edges(gray, centre, radius * (1 + band) + 4)
    at = np.arange(edges.shape[1], dtype=np.float32)[None, :] * step
    window = (at >= rim[:, None] * (1 - band)) & (at <= rim[:, None] * (1 + band))
    masked = np.where(window, edges, -1.0)
    peak = masked.argmax(axis=1)
    strong = masked[np.arange(ANGLES), peak] >= EDGE_FLOOR
    found = peak * step
    return strong & (np.abs(found - rim) <= max(1.5, RIM_TOLERANCE * radius)), found, strong


def _most_in_a_stretch(rays, stretch):
    """The most true entries in any `stretch` neighbouring rays, going around the circle."""
    counts = np.convolve(np.concatenate([rays, rays[: stretch - 1]]).astype(np.int32), np.ones(stretch, np.int32), "valid")
    return int(counts.max())


def _fit_arc(gray, centre, rx, ry):
    """Place a plate of known size by a part of its rim. Returns (x, y, 1.0, share of rays on the rim) or None.

    Every centre within ARC_SHIFT of `centre` is tried on a grid of one pixel: the one for which
    most rays have their strongest edge on the rim is kept, and the rim looked at again from
    there. It must then be the strongest edge on ARC_RAYS of some ARC_STRETCH neighbouring rays.
    The size is not measured: a piece of rim can't give it.
    """
    rim = (rx * ry / np.sqrt((ry * _COS) ** 2 + (rx * _SIN) ** 2)).astype(np.float32)
    radius = max(rx, ry)
    tolerance = max(1.5, RIM_TOLERANCE * radius)
    _, found, strong = _strongest_edge_on_rim(gray, centre, rx, ry, band=ARC_SHIFT)
    if strong.sum() < ARC_RAYS:
        return None
    reach = int(ARC_SHIFT * radius)
    shifts = np.arange(-reach, reach + 1, dtype=np.float32)
    ex, ey = np.meshgrid(shifts, shifts)
    # A centre off by (ex, ey) moves the rim along a ray by about ex * cos + ey * sin.
    expected = rim[None, None, :] + ex[..., None] * _COS[None, None, :] + ey[..., None] * _SIN[None, None, :]
    votes = ((np.abs(expected - found[None, None, :]) <= tolerance) & strong[None, None, :]).sum(axis=2)
    row, column = np.unravel_index(int(votes.argmax()), votes.shape)
    x, y = centre[0] + float(ex[row, column]), centre[1] + float(ey[row, column])

    on_rim = _strongest_edge_on_rim(gray, (x, y), rx, ry)[0]
    if _most_in_a_stretch(on_rim, ARC_STRETCH) < ARC_RAYS:
        return None
    return x, y, 1.0, float(on_rim.sum()) / ANGLES


class BarTracker:
    """Collects a clip's frames with their poses, then reports the plate per frame."""

    def __init__(self, width, height, colour=False):
        """`colour` also keeps a colour copy of every frame, which following by colour needs."""
        self._colours = [] if colour else None  # JPEG bytes of the working image in colour
        self._scale = min(1.0, WORK_LONG_SIDE / max(width, height))
        self._size = (max(round(width * self._scale), 1), max(round(height * self._scale), 1))
        self._frames = []  # JPEG bytes of the grey working image: a clip's worth fits in memory
        self._anchors = []  # mid-shoulder point per frame in working pixels, or None
        self._noses = []  # nose minus mid-shoulder, in working pixels
        self._torsos = []  # shoulder-to-hip lengths in working pixels
        self._last_lab = (None, None)  # the frame last turned into Lab colours, kept for the next look at it
        self._bodies = []  # every body point of the frame in the video's pixels, as rows of (x, y), or None

    def add(self, image, pose):
        """Add the next frame (BGR) and its named landmarks in the video's pixels, or None."""
        gray = cv2.cvtColor(image, cv2.COLOR_BGR2GRAY)
        if self._scale < 1:
            gray = cv2.resize(gray, self._size, interpolation=cv2.INTER_AREA)
        if self._colours is not None:
            small = cv2.resize(image, self._size, interpolation=cv2.INTER_AREA) if self._scale < 1 else image
            self._colours.append(cv2.imencode(".jpg", small, [cv2.IMWRITE_JPEG_QUALITY, JPEG_QUALITY])[1])
        self._frames.append(cv2.imencode(".jpg", gray, [cv2.IMWRITE_JPEG_QUALITY, JPEG_QUALITY])[1])

        self._bodies.append(np.array([(point["x"], point["y"]) for point in pose.values()]) if pose else None)
        shoulders = _midpoint(pose, "left_shoulder", "right_shoulder")
        if shoulders is None:
            self._anchors.append(None)
            return
        self._anchors.append((shoulders[0] * self._scale, shoulders[1] * self._scale))
        hips = _midpoint(pose, "left_hip", "right_hip")
        if hips is not None:
            self._torsos.append(math.dist(shoulders, hips) * self._scale)
        if "nose" in pose:
            nose = pose["nose"]
            self._noses.append(((nose["x"] - shoulders[0]) * self._scale, (nose["y"] - shoulders[1]) * self._scale))

    def finish(self):
        """One BarDetection or None per added frame."""
        results = [None] * len(self._frames)
        posed = [i for i, anchor in enumerate(self._anchors) if anchor is not None]
        if len(posed) < MIN_FRAMES or not self._torsos:
            return results
        # Upright is where the torso looks longest; leaning forward shortens it.
        torso = float(np.percentile(self._torsos, 90))
        heights = np.array([self._anchors[i][1] for i in posed])
        if np.percentile(heights, 95) - np.percentile(heights, 5) < MIN_TRAVEL_PER_TORSO * torso:
            return results

        plate = self._find_plate(posed, heights, torso)
        if plate is None:
            return results
        self._follow_by_shoulders(posed, plate, results)
        return results

    def _follow_by_shoulders(self, posed, plate, results):
        """Measure the found plate in every frame with a pose, looking where it sits by the shoulders."""
        rx, ry, usual = plate
        radius = max(rx, ry)
        offset, offset_frame = usual, None
        for i in posed:
            if offset_frame is None or i - offset_frame > KEEP_OFFSET_FRAMES:
                offset = usual
            gray = self._gray(i)
            fit = self._fit(gray, i, offset, usual, rx, ry)
            if fit is None and offset is not usual:
                fit = self._fit(gray, i, usual, usual, rx, ry)
            if fit is None:
                continue
            x, y, scale, share = fit
            anchor = self._anchors[i]
            offset, offset_frame = (x - anchor[0], y - anchor[1]), i
            results[i] = BarDetection(
                x=x / self._scale,
                y=y / self._scale,
                radius_px=radius * scale / self._scale,
                confidence=share,
            )

    # --- Following a marked plate ---------------------------------------------------
    #
    # 1. The mark gives a centre and one radius. The plate may be an oval, so every shape in
    #    STRETCHES that passes through the rim point is tried on the marked frame, and the one
    #    whose rim is seen best is kept. No rim there: no positions at all.
    # 2. From the marked frame the plate is followed one frame at a time, first forwards, then
    #    backwards. The rim is looked for where the plate would be if it kept moving as it did,
    #    then where it was last.
    # 3. If the rim is at neither place, the picture of the plate from the last frame it was found
    #    in is searched for around that place, further out the longer the plate has been lost, and
    #    the rim measured there. A frame where no rim is measured gets no position.
    #
    # Both options are on by default (Ivan's choice of 2026-11-10, after comparing them on the
    # evaluation clips). With the option "colour", every look at the rim is made first in a picture of the plate's
    # colour, then in the grey one. With "partial rim", a frame where the whole rim isn't found
    # in step 2 or 3 is tried once more for a part of it (`_fit_arc`).

    def follow(self, index, centre, rim, *, colour=None, partial_rim=True, smooth=True):
        """One BarDetection or None per added frame, for the plate marked in frame `index`.

        `index` counts the added frames from 0. `centre` and `rim` are in the video's pixels.

        Both options are on unless turned off; `colour` is on when the tracker kept the colours.

        `colour`: take the plate's colour from the mark and look for the rim first in a picture
        of how much each pixel has that colour, then in the grey picture. Left aside for a run
        when the colour doesn't set the plate apart from what is around it.
        `partial_rim`: in the frames after the mark, where the whole rim isn't found, accept a
        part of it at the size already measured.
        """
        if not 0 <= index < len(self._frames):
            raise MarkError(f"the mark is on frame {index}, and the clip has {len(self._frames)} frames")
        if math.dist(centre, rim) == 0:
            raise MarkError("the mark's centre and rim are the same point")
        if colour is None:
            colour = self._colours is not None
        if colour and self._colours is None:
            raise ValueError("following by colour needs a tracker made with colour=True")
        results = [None] * len(self._frames)
        centre = (centre[0] * self._scale, centre[1] * self._scale)
        rim = (rim[0] * self._scale, rim[1] * self._scale)

        plate_colour = self._plate_colour(index, centre, math.dist(centre, rim)) if colour else None
        views = self._views(index, plate_colour)
        seed = None
        for view in views:
            seed = self._measure_mark(view, centre, rim)
            if seed is not None:
                break
        if seed is None:
            return results
        x, y, rx, ry, share = seed
        results[index] = self._detection(x, y, rx, ry, share)
        patch = self._patch(views[-1], x, y, rx, ry)
        core = self._core(views[-1], x, y, rx, ry)
        for step in (1, -1):
            self._follow_from(index, step, (x, y, rx, ry), patch, results, plate_colour, partial_rim, core)
        return smooth_path(results) if smooth else results

    def _views(self, index, plate_colour):
        """The pictures of frame `index` the rim is looked for in, in order: by colour if there is one, then grey."""
        gray = self._gray(index)
        return [gray] if plate_colour is None else [self._likeness(index, plate_colour), gray]

    def _lab(self, index):
        if self._last_lab[0] != index:
            lab = cv2.cvtColor(cv2.imdecode(self._colours[index], cv2.IMREAD_COLOR), cv2.COLOR_BGR2Lab)
            self._last_lab = (index, lab.astype(np.float32))
        return self._last_lab[1]

    @staticmethod
    def _colour_distance(lab, plate_colour):
        difference = lab - np.float32(plate_colour)  # a new array: `lab` is kept for the next look
        difference[..., 0] *= LIGHTNESS_WEIGHT
        return np.sqrt((difference**2).sum(axis=2))

    def _plate_colour(self, index, centre, reach):
        """The marked plate's colour (Lab), or None when it doesn't set the plate apart from what is around it."""
        lab = self._lab(index)
        ys, xs = np.mgrid[0 : lab.shape[0], 0 : lab.shape[1]]
        out = np.hypot(xs - centre[0], ys - centre[1]) / reach
        on_plate = (out >= COLOUR_SAMPLE[0]) & (out <= COLOUR_SAMPLE[1])
        around = (out >= COLOUR_NEAR[0]) & (out <= COLOUR_NEAR[1])
        if on_plate.sum() < 20 or around.sum() < 20:
            return None
        plate_colour = np.median(lab[on_plate], axis=0)
        alike = self._colour_distance(lab, plate_colour) <= COLOUR_REACH
        if alike[on_plate].mean() < MIN_COLOUR_ON_PLATE or alike[around].mean() > MAX_COLOUR_AROUND:
            return None
        return tuple(float(v) for v in plate_colour)

    def _likeness(self, index, plate_colour):
        """A grey picture of frame `index`: white where a pixel has the plate's colour, black where it is far from it."""
        distance = self._colour_distance(self._lab(index), plate_colour)
        alike = np.clip((1.25 * COLOUR_REACH - distance) / (0.5 * COLOUR_REACH), 0.0, 1.0) * 255
        return cv2.GaussianBlur(alike.astype(np.uint8), (3, 3), 0)

    # --- Finding the plate from proposals ----------------------------------------------
    #
    # 1. A few frames are shown to the proposer, which answers with boxes. Boxes of the wrong
    #    shape or size for a plate are dropped; in the others the rim is measured as for a mark,
    #    and a box without a rim is dropped.
    # 2. The boxes with the best rims are followed through the clip like marked plates.
    # 3. A track is kept only if it is seen in enough frames and goes up and down by itself:
    #    that tells the loaded plate from plates on a rack and from a shoe. The lifter's
    #    movement is not asked about, since in a clean or a snatch the bar doesn't travel with
    #    the shoulders. It must also stay near the lifter, whose hands are on the bar: a plate
    #    moving at the other side of the picture is somebody else's. And it must keep its size:
    #    a circle that grows and shrinks is sliding over something that is not a plate.
    # 4. The best track gives the positions. If a larger circle is about the same centre, the
    #    track is on a ring or a smaller plate in front of the plate: such a circle is followed
    #    too, and if it passes step 3 its size is reported, at the best track's positions.
    #    No track kept: no positions.

    def find(self, proposer):
        """One BarDetection or None per added frame, for the plate found among `proposer`'s boxes.

        `proposer.propose(image)` takes a BGR picture and returns boxes in its pixels as
        (left, top, right, bottom, score).
        """
        if self._colours is None:
            raise ValueError("finding from proposals needs a tracker made with colour=True")
        count = len(self._frames)
        results = [None] * count
        if not self._torsos:
            return results
        torso = float(np.percentile(self._torsos, 90))

        candidates = []
        for index in sorted({count * (k + 1) // (FIND_FRAMES + 1) for k in range(FIND_FRAMES)}):
            gray = self._gray(index)
            taken = []
            for left, top, right, bottom, score in proposer.propose(cv2.imdecode(self._colours[index], cv2.IMREAD_COLOR)):
                width, height = right - left, bottom - top
                if score < MIN_BOX_SCORE or height < MIN_BOX_HEIGHT or not BOX_SHAPES[0] <= width / height <= BOX_SHAPES[1]:
                    continue
                radius = height / 2
                if not FIND_RADIUS_PER_TORSO[0] * torso <= radius <= FIND_RADIUS_PER_TORSO[1] * torso:
                    continue
                centre = ((left + right) / 2, (top + bottom) / 2)
                if any(math.dist(centre, c) < SAME_PLACE * radius and abs(radius / r - 1) < SAME_SIZE for c, r in taken):
                    continue
                taken.append((centre, radius))
                rim = (centre[0], centre[1] - radius)
                measured = self._measure_mark(gray, centre, rim)
                if measured is None:
                    # A plate about as bright as what is behind it has no rim in the grey picture.
                    plate_colour = self._plate_colour(index, centre, radius)
                    if plate_colour is not None:
                        measured = self._measure_mark(self._likeness(index, plate_colour), centre, rim)
                if measured is not None:
                    candidates.append((measured[4] * (0.5 + score), index, measured))

        def kept(bars):
            """A followed track as (score, radius, positions), or None if it is not the plate being lifted."""
            found = [bar for bar in bars if bar is not None]
            if len(found) < MIN_FOUND * count or self._travel(found) < MIN_TRAVEL:
                return None
            if self._size_spread(found) > MAX_SIZE_SPREAD:
                return None
            if self._from_lifter(bars) > MAX_FROM_LIFTER * torso / self._scale:
                return None
            seen = len(found) / count * float(np.median([bar.confidence for bar in found]))
            size = float(np.median([bar.radius_px for bar in found]))
            return seen * (1 + 0.5 * size * self._scale / torso), size, bars

        def follow(index, x, y, rx, ry):
            centre = (x / self._scale, y / self._scale)
            return self.follow(index, centre, (centre[0], centre[1] - ry / self._scale), smooth=False)

        candidates = [(index, measured) for _, index, measured in sorted(candidates, key=lambda c: -c[0])]
        tracks = []
        followed = []
        waiting = []  # candidates not followed in the first round
        for index, (x, y, rx, ry, _) in candidates:
            radius = max(rx, ry) / self._scale
            centre = (x / self._scale, y / self._scale)
            if any(self._same_circle(bars[index], centre, radius) for bars in followed):
                continue
            if len(followed) == FIND_FOLLOWS:
                waiting.append((index, (x, y, rx, ry)))
                continue
            followed.append(follow(index, x, y, rx, ry))
            track = kept(followed[-1])
            if track is not None:
                tracks.append(track)
        if not tracks:
            return results
        best = max(tracks, key=lambda track: track[0])
        # A larger circle about the best track's centre may be waiting: the plate itself, when the
        # track is on a ring or a smaller plate in front of it. The largest such is followed too.
        around = [
            (max(rx, ry) / self._scale, index, (x, y, rx, ry))
            for index, (x, y, rx, ry) in waiting
            if (bar := best[2][index]) is not None
            and max(rx, ry) / self._scale > (1 + SAME_SIZE) * bar.radius_px
            and math.dist((bar.x, bar.y), (x / self._scale, y / self._scale)) < SAME_PLACE * max(rx, ry) / self._scale
        ]
        if around:
            _, index, plate = max(around, key=lambda c: c[0])
            track = kept(follow(index, *plate))
            if track is not None:
                tracks.append(track)
        # Of circles about one centre the largest is the plate. Its rim is often seen in fewer
        # frames than a ring inside it, so the best track keeps its positions and takes the size.
        larger = 1.0
        for _, size, bars in sorted(tracks, key=lambda track: -track[1]):
            if size > (1 + SAME_SIZE) * best[1] and self._about_one_centre(bars, best[2]):
                larger = float(np.median([a.radius_px / b.radius_px for a, b in zip(bars, best[2]) if a and b]))
                break
        # What the follower left out or got wrong for a few frames is mended once, on the chosen
        # track: stray runs dropped, gaps followed again, and the whole smoothed.
        mended = smooth_path(drop_islands(self._fill_gaps(drop_islands(best[2]))))
        return [bar and replace(bar, radius_px=bar.radius_px * larger) for bar in mended]

    def _fill_gaps(self, bars):
        """`bars` with the frames it has no position for filled in, where following again finds the plate there.

        The follower goes by what the plate looked like where it started. Once it loses the
        plate (more of it has come out from behind a rack's upright, say) it may stay lost for
        many frames that a fresh look finds. So for each gap, longest first, the plate is
        followed again from a frame near the gap: the one before it, the one after it, then
        frames a little further off, since the rim is often poorly seen right where it was
        lost. The new track is used only where the first has nothing, and only if the two are
        about the same circle wherever both have a position. At most FILL_FOLLOWS such tracks
        are made for a clip.
        """
        bars = list(bars)
        runs = _runs(bars)
        gaps = sorted(((b - a, a, b) for (_, a), (b, _) in itertools.pairwise(runs) if b - a >= MIN_GAP), reverse=True)
        follows = 0
        for _, start, end in gaps:
            for step in FILL_FROM:
                for seed in (start - 1 - step, end + step):
                    if follows == FILL_FOLLOWS or all(bar is not None for bar in bars[start:end]):
                        break
                    if not 0 <= seed < len(bars) or bars[seed] is None:
                        continue
                    at = bars[seed]
                    again = self.follow(seed, (at.x, at.y), (at.x, at.y - at.radius_px), smooth=False)
                    both = [(a, b) for a, b in zip(bars, again) if a and b]
                    if not both:
                        continue  # no rim was measured at that frame: nothing was followed
                    follows += 1
                    if len(both) < MIN_SHARED_FRAMES:
                        continue
                    apart = float(np.median([math.dist((a.x, a.y), (b.x, b.y)) / a.radius_px for a, b in both]))
                    size = float(np.median([b.radius_px / a.radius_px for a, b in both]))
                    if apart < SAME_PLACE and abs(size - 1) < SAME_SIZE:
                        bars = [a or b for a, b in zip(bars, again)]
        return bars

    @staticmethod
    def _same_circle(bar, centre, radius):
        return (
            bar is not None
            and math.dist((bar.x, bar.y), centre) < SAME_PLACE * radius
            and abs(bar.radius_px / radius - 1) < SAME_SIZE
        )

    @staticmethod
    def _about_one_centre(larger, smaller):
        apart = [math.dist((a.x, a.y), (b.x, b.y)) / a.radius_px for a, b in zip(larger, smaller) if a and b]
        return len(apart) >= MIN_SHARED_FRAMES and float(np.median(apart)) < SAME_PLACE

    @staticmethod
    def _travel(found):
        """How far a track goes up and down, in its own radii. The few highest and lowest positions are left out."""
        heights = [bar.y for bar in found]
        radius = float(np.median([bar.radius_px for bar in found]))
        return float(np.percentile(heights, 95) - np.percentile(heights, 5)) / radius

    @staticmethod
    def _size_spread(found):
        """A track's larger sizes over its smaller ones, the few largest and smallest left out."""
        sizes = [bar.radius_px for bar in found]
        return float(np.percentile(sizes, 95) / np.percentile(sizes, 5))

    def _from_lifter(self, bars):
        """How far a track usually is from the lifter's nearest body point, in the video's pixels."""
        apart = [
            float(np.hypot(body[:, 0] - bar.x, body[:, 1] - bar.y).min())
            for body, bar in zip(self._bodies, bars)
            if body is not None and bar is not None
        ]
        return float(np.median(apart)) if len(apart) >= MIN_SHARED_FRAMES else math.inf

    def _detection(self, x, y, rx, ry, share):
        return BarDetection(
            x=x / self._scale, y=y / self._scale, radius_px=max(rx, ry) / self._scale, confidence=share
        )

    def _measure_mark(self, gray, centre, rim):
        """The plate at the mark as (x, y, rx, ry, share of the rim seen), or None if no rim is there.

        Two looks. The first tries every shape through the two marked points, and gives a centre
        that is better than the click even when the shape is not yet right. The second tries every
        shape again from that centre, at the size the first look measured.
        """
        reach = math.dist(centre, rim)
        angle = math.atan2(rim[1] - centre[1], rim[0] - centre[0])
        # For each shape, rx = stretch * ry, the oval that passes through the rim point.
        through_rim = [reach * math.hypot(math.cos(angle), stretch * math.sin(angle)) / stretch for stretch in STRETCHES]
        first = self._best_shape(gray, centre, through_rim)
        if first is None:
            return None
        x, y, rx, ry, _ = first
        same_size = [math.sqrt(rx * ry / stretch) for stretch in STRETCHES]
        best = self._best_shape(gray, (x, y), same_size) or first
        # What was measured must still be what was marked: near the click, and about as far out towards the rim click.
        x, y, rx, ry, _ = best
        towards_rim = rx * ry / math.hypot(ry * math.cos(angle), rx * math.sin(angle))
        if math.dist((x, y), centre) > MAX_MARK_OFF * reach or abs(towards_rim / reach - 1) > MAX_MARK_SCALE:
            return None
        return best

    def _best_shape(self, gray, centre, vertical_radii):
        """The shape in STRETCHES whose rim is seen best at `centre`, as (x, y, rx, ry, share); None if none is seen."""
        best = None
        for stretch, ry in zip(STRETCHES, vertical_radii, strict=True):
            rx = ry * stretch
            if max(rx, ry) < MIN_MARK_RADIUS:
                continue
            fit = _fit_checked(gray, centre, rx, ry)
            if fit is None:
                continue
            x, y, scale, share = fit
            if abs(scale - 1) > MAX_MARK_SCALE:
                continue
            if best is None or share > best[4]:
                best = (x, y, rx * scale, ry * scale, share)
        return best

    def _follow_from(self, start, step, plate, patch, results, plate_colour=None, partial_rim=False, core=None):
        """Follow `plate` (x, y, rx, ry) from frame `start` to the clip's end (`step` 1) or start (`step` -1)."""
        x, y, rx, ry = plate
        largest, smallest = max(rx, ry) * MAX_TOTAL_SCALE, max(rx, ry) / MAX_TOTAL_SCALE
        sizes = [max(rx, ry)]  # of the last found frames; a plate keeps its size from one frame to the next
        moved = (0.0, 0.0)
        missed = 0
        on_middle = core is not None  # the picture of the middle was cut at the frame this starts from
        for i in range(start + step, len(self._frames) if step > 0 else -1, step):
            views = self._views(i, plate_colour)
            gray = views[-1]
            places = [(x + moved[0], y + moved[1])]
            if moved != (0.0, 0.0):
                places.append((x, y))
            usual = float(np.median(sizes))
            fit = self._fit_step(views, places, rx, ry, smallest, largest, usual)
            if fit is None:
                reach = min(1 + REACH_PER_MISSED_FRAME * missed, MAX_REACH) * max(rx, ry)
                place = self._match(gray, patch, (x, y), reach)
                if place is not None:
                    fit = self._fit_step(views, [place], rx, ry, smallest, largest, usual)
                    places.append(place)
            if fit is None and partial_rim:
                fit = next(
                    (arc for place in places for view in views if (arc := _fit_arc(view, place, rx, ry)) is not None),
                    None,
                )
            if core is not None:
                fit, on_middle = self._settle_on_core(views, core, fit, places, rx, ry, missed, on_middle)
            if fit is None:
                missed += 1
                moved = (0.0, 0.0)
                continue
            new_x, new_y, scale, share = fit
            moved = (new_x - x, new_y - y) if missed == 0 else (0.0, 0.0)
            x, y, rx, ry = new_x, new_y, rx * scale, ry * scale
            sizes = [*sizes[-(SIZE_MEMORY - 1) :], max(rx, ry)]
            missed = 0
            patch = self._patch(gray, x, y, rx, ry)
            results[i] = self._detection(x, y, rx, ry, share)

    def _fit_step(self, views, places, rx, ry, smallest, largest, usual):
        """The whole rim near one of `places` in one of `views`, if it is the followed plate's size; None otherwise.

        `usual` is the size the plate had over the last found frames. The rim must be close to
        that, not just to the frame before: frame-to-frame steps add up, and a run of rims each
        a little off (an inner ring, a blurred edge) would walk the circle off the plate.
        """
        for place in places:
            for view in views:
                fit = _fit_checked(view, place, rx, ry)
                if fit is None:
                    continue
                size = max(rx, ry) * fit[2]
                if abs(size / usual - 1) <= MAX_STEP_SCALE and smallest <= size <= largest:
                    return fit
        return None

    def _settle_on_core(self, views, core, fit, places, rx, ry, missed, held):
        """Put a frame's circle on the plate's middle, where that is seen. Returns (a fit like `_fit_rim` or None, was the middle used).

        A rim that is partly hidden, or has the plate's own side next to it in an oblique view,
        gives a centre that is off towards the part still seen. The middle is looked for near the
        rim's centre, and without a rim near where the plate was expected, at the size it had.
        Where it is found, a piece of the rim must lie on the circle about it: the middle says
        where the plate is, the rim that it is still the plate.

        `held` says the middle was used in the frame before. It is then first looked for close
        to where the plate is expected, and may look less like the picture: over a lift the
        light on the plate changes, and a circle that goes by the middle in some frames and by
        the rim in others steps aside each time it changes over.
        """
        gray = views[-1]
        scale, share = (1.0, None) if fit is None else fit[2:]
        if held:
            at = self._core_at(gray, core, places[0], rx * scale, ry * scale, CORE_STEP * max(rx, ry), KEEP_CORE_MATCH)
            seen = None if at is None else self._rim_piece(views, at, rx * scale, ry * scale)
            if seen is not None:
                return (at[0], at[1], scale, seen if share is None else share), True
        if fit is not None:
            x, y = fit[:2]
            at = self._core_at(gray, core, (x, y), rx * scale, ry * scale, CORE_REACH * max(rx, ry))
            if at is None or self._rim_piece(views, at, rx * scale, ry * scale) is None:
                return fit, False
            return (at[0], at[1], scale, share), True
        reach = min(CORE_REACH * (1 + missed), MAX_CORE_REACH) * max(rx, ry)
        for place in places:
            at = self._core_at(gray, core, place, rx, ry, reach)
            share = None if at is None else self._rim_piece(views, at, rx, ry)
            if share is not None:
                return (at[0], at[1], 1.0, share), True
        return None, False

    @staticmethod
    def _rim_piece(views, centre, rx, ry):
        """The share of the rim seen on the circle about `centre`, if a piece of it is there as for `_fit_arc`; else None."""
        for view in views:
            on_rim = _strongest_edge_on_rim(view, centre, rx, ry, band=SEARCH_BANDS[1])[0]
            if _most_in_a_stretch(on_rim, ARC_STRETCH) >= ARC_RAYS:
                return float(on_rim.sum()) / ANGLES
        return None

    @staticmethod
    def _core(gray, x, y, rx, ry):
        """The picture of the plate's middle at (x, y), with the radii it was cut at: (image, rx, ry); None if it is too flat."""
        size = (2 * max(round(CORE * rx), 3) + 1, 2 * max(round(CORE * ry), 3) + 1)
        image = cv2.getRectSubPix(gray, size, (x, y))
        return (image, rx, ry) if float(image.std()) >= MIN_CORE_CONTRAST else None

    @staticmethod
    def _core_at(gray, core, centre, rx, ry, reach, least=MIN_CORE_MATCH):
        """The plate's centre within `reach` of `centre`, going by its middle; None if nothing there looks at least `least` like it."""
        image, cut_rx, cut_ry = core
        if abs(rx / cut_rx - 1) > SAME_CORE_SIZE or abs(ry / cut_ry - 1) > SAME_CORE_SIZE:
            size = (2 * max(round(CORE * rx), 3) + 1, 2 * max(round(CORE * ry), 3) + 1)
            image = cv2.resize(image, size, interpolation=cv2.INTER_AREA if rx < cut_rx else cv2.INTER_LINEAR)
        half_y, half_x = image.shape[0] // 2, image.shape[1] // 2
        height, width = gray.shape
        cx, cy, reach = round(centre[0]), round(centre[1]), math.ceil(reach)
        left, right = max(cx - half_x - reach, 0), min(cx + half_x + reach + 1, width)
        top, bottom = max(cy - half_y - reach, 0), min(cy + half_y + reach + 1, height)
        window = gray[top:bottom, left:right]
        if window.shape[0] < image.shape[0] or window.shape[1] < image.shape[1]:
            return None
        likeness = cv2.matchTemplate(window, image, cv2.TM_CCOEFF_NORMED)
        _, best, _, (column, row) = cv2.minMaxLoc(likeness)
        if not best >= least:  # also refuses NaN, which a flat picture gives
            return None
        # Between pixels: the top of a parabola through the best place and its two neighbours, each way.
        shift = [0.0, 0.0]
        for axis, (at, line) in enumerate(((column, likeness[row, :]), (row, likeness[:, column]))):
            if 0 < at < len(line) - 1:
                before, here, after = (float(v) for v in line[at - 1 : at + 2])
                bend = before - 2 * here + after
                if bend < 0:
                    shift[axis] = float(np.clip(0.5 * (before - after) / bend, -0.5, 0.5))
        return (left + column + half_x + shift[0], top + row + half_y + shift[1])

    def _patch(self, gray, x, y, rx, ry):
        """The picture of the plate at (x, y), with where its centre sits in it: (image, (dx, dy))."""
        height, width = gray.shape
        left, right = max(int(x - PATCH_MARGIN * rx), 0), min(int(x + PATCH_MARGIN * rx) + 1, width)
        top, bottom = max(int(y - PATCH_MARGIN * ry), 0), min(int(y + PATCH_MARGIN * ry) + 1, height)
        return gray[top:bottom, left:right].copy(), (x - left, y - top)

    def _match(self, gray, patch, centre, reach):
        """Where the plate's picture fits best within `reach` of `centre`, as a centre; None if nowhere fits well."""
        image, (dx, dy) = patch
        height, width = gray.shape
        left, right = max(int(centre[0] - dx - reach), 0), min(int(centre[0] - dx + image.shape[1] + reach) + 1, width)
        top, bottom = max(int(centre[1] - dy - reach), 0), min(int(centre[1] - dy + image.shape[0] + reach) + 1, height)
        window = gray[top:bottom, left:right]
        if min(image.shape) < 3 or window.shape[0] < image.shape[0] or window.shape[1] < image.shape[1]:
            return None
        likeness = cv2.matchTemplate(window, image, cv2.TM_CCOEFF_NORMED)
        _, best, _, at = cv2.minMaxLoc(likeness)
        if not best >= MIN_MATCH:  # also refuses NaN, which a flat picture gives
            return None
        return (left + at[0] + dx, top + at[1] + dy)

    def _gray(self, index):
        return cv2.imdecode(self._frames[index], cv2.IMREAD_GRAYSCALE)

    def _fit(self, gray, index, offset, usual, rx, ry):
        """The rim in frame `index`, looked for at `offset` from the shoulders; None if it isn't a believable plate."""
        anchor = self._anchors[index]
        fit = _fit_rim(gray, (anchor[0] + offset[0], anchor[1] + offset[1]), rx, ry)
        if fit is None:
            return None
        x, y, scale, _ = fit
        drift = math.dist((x - anchor[0], y - anchor[1]), usual)
        if abs(scale - 1) > MAX_SCALE_CHANGE or drift > MAX_DRIFT * max(rx, ry):
            return None
        return fit

    def _spread(self, posed, heights, count):
        """Up to `count` posed frames, spread over the lifter's heights so that standing still doesn't dominate."""
        order = np.argsort(heights)
        levels = np.linspace(heights.min(), heights.max(), count)
        picks = {int(order[min(np.searchsorted(heights[order], level), len(order) - 1)]) for level in levels}
        return [posed[k] for k in sorted(picks)]

    def _aligned_median(self, frames, torso):
        """The median of `frames` after moving each so its shoulders sit at the centre. Returns (image, centre)."""
        half_w, half_h = int(ALIGN_HALF_WIDTH * torso), int(ALIGN_HALF_HEIGHT * torso)
        size = (2 * half_w + 1, 2 * half_h + 1)
        moved = []
        for i in frames:
            x, y = self._anchors[i]
            shift = np.float32([[1, 0, half_w - x], [0, 1, half_h - y]])
            moved.append(cv2.warpAffine(self._gray(i), shift, size, borderMode=cv2.BORDER_REPLICATE))
        return np.median(np.stack(moved), axis=0).astype(np.uint8), (half_w, half_h)

    def _proposals(self, image, centre, torso):
        """Round and oval shapes in the aligned image, best rim first: (offset from the shoulders, rx, ry)."""
        low, high = max(MIN_RADIUS_PER_TORSO * torso, 6.0), MAX_RADIUS_PER_TORSO * torso
        nose = np.median(np.array(self._noses), axis=0) if self._noses else None
        fits = []
        for stretch in STRETCHES:
            # Widening the image by 1/stretch turns an oval of that shape into a circle.
            wide = image if stretch == 1.0 else cv2.resize(image, None, fx=1 / stretch, fy=1)
            circles = cv2.HoughCircles(
                cv2.GaussianBlur(wide, (5, 5), 1.2),
                cv2.HOUGH_GRADIENT,
                dp=HOUGH_DP,
                minDist=low / 2,
                param1=HOUGH_EDGE,
                param2=HOUGH_VOTES,
                minRadius=int(low),
                maxRadius=math.ceil(high),
            )
            if circles is None:
                continue
            for x, y, r in circles[0, :CIRCLES_PER_STRETCH]:
                fit = _fit_rim(image, (float(x) * stretch, float(y)), float(r) * stretch, float(r))
                if fit is not None:
                    fx, fy, scale, share = fit
                    fits.append((share, (fx - centre[0], fy - centre[1]), float(r) * stretch * scale, float(r) * scale))

        proposals = []
        for _, offset, rx, ry in sorted(fits, reverse=True):
            if nose is not None and math.dist(offset, nose) < HEAD_REACH * max(rx, ry):
                continue
            if any(math.dist(offset, o) < 0.3 * ry and abs(ry / other - 1) < 0.15 for o, _, other in proposals):
                continue
            proposals.append((offset, rx, ry))
            if len(proposals) == MAX_PROPOSALS:
                break
        return proposals

    def _find_plate(self, posed, heights, torso):
        """The plate's horizontal and vertical radii and its usual place by the shoulders, or None."""
        image, centre = self._aligned_median(self._spread(posed, heights, ALIGN_FRAMES), torso)
        trial = self._spread(posed, heights, TRIAL_FRAMES)
        grays = [self._gray(i) for i in trial]

        best = None
        for offset, rx, ry in self._proposals(image, centre, torso):
            fits = [(i, self._fit(gray, i, offset, offset, rx, ry)) for i, gray in zip(trial, grays)]
            fits = [(i, fit) for i, fit in fits if fit is not None]
            found = len(fits) / len(trial)
            if found < MIN_TRIAL_FOUND or len(fits) < MIN_FRAMES:
                continue
            score = found * float(np.median([fit[3] for _, fit in fits]))
            if best is None or score > best[0]:
                places = np.array([(fit[0] - self._anchors[i][0], fit[1] - self._anchors[i][1]) for i, fit in fits])
                usual = np.median(places, axis=0)
                best = (score, rx, ry, (float(usual[0]), float(usual[1])))
        return None if best is None else best[1:]
