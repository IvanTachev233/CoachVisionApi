import itertools
import shutil
import subprocess

import cv2
import numpy as np
import pytest
from conftest import write_clip

from vision_worker.video import VideoError, VideoReader, count_frames

FFMPEG = shutil.which("ffmpeg")


def marker_frames(count=12, width=64, height=48):
    """Dark frames with a bright block in the top-left corner, so orientation can be checked."""
    frames = []
    for _ in range(count):
        frame = np.full((height, width, 3), 20, dtype=np.uint8)
        frame[: height // 2, : width // 2] = 235
        frames.append(frame)
    return frames


def brightest_quadrant(image):
    gray = image if image.ndim == 2 else cv2.cvtColor(image, cv2.COLOR_BGR2GRAY)
    h, w = gray.shape
    quadrants = {
        "top-left": gray[: h // 2, : w // 2],
        "top-right": gray[: h // 2, w // 2 :],
        "bottom-left": gray[h // 2 :, : w // 2],
        "bottom-right": gray[h // 2 :, w // 2 :],
    }
    return max(quadrants, key=lambda name: quadrants[name].mean())


def test_reads_every_frame_in_order_with_its_time(tmp_path):
    clip = write_clip(tmp_path / "clip.mp4", marker_frames(count=12), fps=30.0)

    with VideoReader(clip) as reader:
        frames = list(reader)

    assert [frame.index for frame in frames] == list(range(12))
    assert all(frame.image.shape == (48, 64, 3) for frame in frames)
    times = [frame.t for frame in frames]
    assert times[0] == pytest.approx(0.0, abs=1e-6)
    assert all(later > earlier for earlier, later in itertools.pairwise(times))
    assert times[1] == pytest.approx(1 / 30, abs=0.005)


def test_summary_describes_the_decoded_video(tmp_path):
    clip = write_clip(tmp_path / "clip.mp4", marker_frames(count=12), fps=30.0)

    with VideoReader(clip) as reader:
        for _ in reader:
            pass
        summary = reader.summary()

    assert summary["width"] == 64
    assert summary["height"] == 48
    assert summary["frameCount"] == 12
    assert summary["frameRate"] == pytest.approx(30.0, rel=0.02)
    assert summary["durationS"] == pytest.approx(12 / 30, rel=0.05)


@pytest.mark.skipif(FFMPEG is None, reason="needs the ffmpeg command to write rotation metadata")
def test_applies_the_rotation_tag_like_a_player_does(tmp_path):
    plain = write_clip(tmp_path / "plain.mp4", marker_frames(), fps=30.0)
    rotated = tmp_path / "rotated.mp4"
    reference = tmp_path / "reference.png"
    quiet = [FFMPEG, "-v", "error", "-y"]
    # The pixels stay 64x48; only a tag says "show this turned by 90 degrees", as phones write it.
    subprocess.run([*quiet, "-display_rotation", "90", "-i", str(plain), "-c", "copy", str(rotated)], check=True)
    # ffmpeg applies the tag when it decodes, which makes its frame the reference.
    subprocess.run([*quiet, "-i", str(rotated), "-frames:v", "1", str(reference)], check=True)
    expected = cv2.imread(str(reference))

    with VideoReader(rotated) as reader:
        first = next(iter(reader))
        size = (reader.width, reader.height)

    assert first.image.shape == expected.shape == (64, 48, 3)
    assert size == (48, 64)
    assert brightest_quadrant(first.image) == brightest_quadrant(expected)
    assert brightest_quadrant(first.image) != "top-left"


def test_missing_file_is_a_video_error(tmp_path):
    with pytest.raises(VideoError, match="clip.mp4"):
        VideoReader(tmp_path / "clip.mp4")


def test_file_that_is_not_a_video_is_a_video_error(tmp_path):
    not_video = tmp_path / "notes.mp4"
    not_video.write_text("this is not a video")

    with pytest.raises(VideoError), VideoReader(not_video) as reader:
        list(reader)
        reader.summary()


def test_count_frames_matches_what_the_reader_yields(tmp_path):
    clip = write_clip(tmp_path / "clip.mp4", marker_frames(count=17), fps=30.0)

    with VideoReader(clip) as reader:
        decoded = len(list(reader))

    assert count_frames(clip) == decoded == 17


def test_count_frames_of_a_missing_file_is_a_video_error(tmp_path):
    with pytest.raises(VideoError, match="absent.mp4"):
        count_frames(tmp_path / "absent.mp4")


def numbered_frames(count=20, width=64, height=48):
    """Frames that differ from each other: a bright column that moves one step per frame."""
    frames = []
    for i in range(count):
        frame = np.full((height, width, 3), 20, dtype=np.uint8)
        frame[:, i * 3 : i * 3 + 3] = 235
        frames.append(frame)
    return frames


def test_only_the_asked_frames_are_given_with_the_same_number_time_and_picture(tmp_path):
    clip = write_clip(tmp_path / "clip.mp4", numbered_frames(), fps=30.0)
    with VideoReader(clip) as reader:
        every = list(reader)

    with VideoReader(clip) as reader:
        some = list(reader.frames(only={3, 11, 18}))
        summary = reader.summary()

    assert [frame.index for frame in some] == [3, 11, 18]
    for frame in some:
        assert frame.t == every[frame.index].t
        assert np.array_equal(frame.image, every[frame.index].image)
    # The frames in between were still counted.
    assert summary["frameCount"] == 20


def test_asking_for_no_frames_gives_none(tmp_path):
    clip = write_clip(tmp_path / "clip.mp4", numbered_frames(), fps=30.0)

    with VideoReader(clip) as reader:
        assert list(reader.frames(only=set())) == []
