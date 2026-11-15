"""One pass over a clip: pose per frame, then the bar track, the pose smoothed, then the raw-output document."""

from vision_worker.bar import (
    BAR_DETECTOR,
    BarTracker,
    MarkError,
    marked_detector,
    proposed_detector,
)
from vision_worker.raw_output import build_document
from vision_worker.smooth import PLANTED_LIFTS, smooth_pose

# A mark's time should be a frame's time; this much difference is allowed for rounding on the way.
MARK_TIME_SLACK_S = 0.1


def analyse(reader, pose, *, job_id, pose_model, mark=None, colour=True, partial_rim=True, proposer=None, lift=None):
    """Run `pose` (anything with `detect(image, t)`) and the bar tracker over every frame of `reader`.

    Without a `mark` (a `bar.Mark`) the plate is found automatically; with one, the marked plate
    is followed from the frame nearest the mark's time. `colour` and `partial_rim` are the
    follower's options (`BarTracker.follow`), on unless turned off; they only matter with a mark.
    Without a mark and with a `proposer` (anything with `propose(image)` and a `name`), the plate
    is found among its boxes (`BarTracker.find`). `lift` is the lift the job names, if it names
    one: in a lift that keeps the feet planted, the ankles are held where they stand.
    """
    tracker = None
    rows = []
    for frame in reader:
        if tracker is None:
            height, width = frame.image.shape[:2]
            tracker = BarTracker(
                width, height, colour=(colour and mark is not None) or (mark is None and proposer is not None)
            )
        landmarks = pose.detect(frame.image, frame.t)
        tracker.add(frame.image, landmarks)
        rows.append((frame.index, frame.t, landmarks))

    video = reader.summary()  # raises VideoError when no frame was decoded
    if mark is None and proposer is not None:
        bars = tracker.find(proposer)
        detector = proposed_detector(proposer)
    elif mark is None:
        bars = tracker.finish()
        detector = BAR_DETECTOR
    else:
        nearest = min(range(len(rows)), key=lambda i: abs(rows[i][1] - mark.t))
        if abs(rows[nearest][1] - mark.t) > MARK_TIME_SLACK_S:
            raise MarkError(f"the mark is at {mark.t} s, and the video has no frame near that time")
        bars = tracker.follow(nearest, mark.centre, mark.rim, colour=colour, partial_rim=partial_rim)
        detector = marked_detector(colour, partial_rim)
    # The bar tracker went by the body points as measured; what is written is smoothed over time.
    poses = smooth_pose([landmarks for _, _, landmarks in rows], feet_planted=lift in PLANTED_LIFTS)
    return build_document(
        job_id=job_id,
        pose_model=pose_model,
        video=video,
        frames=[(index, t, bar, landmarks) for (index, t, _), bar, landmarks in zip(rows, bars, poses)],
        bar_detector=detector,
    )
