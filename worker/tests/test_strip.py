"""Copies of clips without sound, location or other tags (vision_eval.strip)."""

import json
import shutil
import subprocess

import pytest

from vision_eval import strip

needs_ffmpeg = pytest.mark.skipif(
    not (shutil.which("ffmpeg") and shutil.which("ffprobe")), reason="ffmpeg and ffprobe not installed"
)


def _probe(path):
    out = subprocess.run(
        ["ffprobe", "-v", "error", "-show_streams", "-show_format", "-of", "json", str(path)],
        check=True,
        capture_output=True,
        text=True,
    ).stdout
    return json.loads(out)


def _clip_with_sound_and_location(path, rotate=None):
    """A 1-second clip with a video stream, a sound stream and a location tag, as a phone writes them."""
    encoded = path.with_name(f"encoded-{path.name}") if rotate is not None else path
    command = [
        "ffmpeg", "-v", "error", "-y",
        "-f", "lavfi", "-i", "testsrc=size=64x48:rate=10:duration=1",
        "-f", "lavfi", "-i", "sine=frequency=440:duration=1",
        "-metadata", "location=+51.5074-000.1278/",
        "-metadata", "title=gym session",
        "-c:v", "libx264", "-pix_fmt", "yuv420p", "-c:a", "aac", "-shortest",
        str(encoded),
    ]  # fmt: skip
    subprocess.run(command, check=True)
    if rotate is not None:
        # The rotation is set on a stream copy, as a phone stores it: the pixels stay unrotated.
        command = ["ffmpeg", "-v", "error", "-y", "-display_rotation", str(rotate), "-i", str(encoded), "-c", "copy"]
        subprocess.run([*command, str(path)], check=True)
        encoded.unlink()


@needs_ffmpeg
def test_strip_keeps_only_the_video_and_drops_every_tag(tmp_path):
    source = tmp_path / "in" / "clip.mp4"
    source.parent.mkdir()
    _clip_with_sound_and_location(source)
    before = _probe(source)
    assert {s["codec_type"] for s in before["streams"]} == {"video", "audio"}
    assert "location" in before["format"]["tags"]

    out = strip.strip_clip(source, tmp_path / "out" / "clip.mp4")

    after = _probe(out)
    assert [s["codec_type"] for s in after["streams"]] == ["video"]
    tags = after["format"].get("tags", {})
    assert not any("location" in key.lower() or key == "title" for key in tags)
    assert after["streams"][0]["nb_frames"] == before["streams"][0]["nb_frames"]


@needs_ffmpeg
def test_strip_keeps_the_rotation(tmp_path):
    source = tmp_path / "clip.mp4"
    _clip_with_sound_and_location(source, rotate=90)

    out = strip.strip_clip(source, tmp_path / "out" / "clip.mp4")

    assert strip.rotation(out) == strip.rotation(source) != 0


@needs_ffmpeg
def test_check_refuses_a_file_that_still_has_sound_or_location(tmp_path):
    source = tmp_path / "clip.mp4"
    _clip_with_sound_and_location(source)
    with pytest.raises(strip.StripError, match="audio|location"):
        strip.check_stripped(source)


@needs_ffmpeg
def test_strip_folder_writes_every_clip_and_never_overwrites_the_source(tmp_path):
    clips = tmp_path / "clips"
    clips.mkdir()
    for name in ("a.mp4", "b.mp4"):
        _clip_with_sound_and_location(clips / name)
    (clips / "labels.csv").write_text("file\n")

    written = strip.strip_folder(clips, tmp_path / "stripped")

    assert sorted(p.name for p in written) == ["a.mp4", "b.mp4"]
    with pytest.raises(strip.StripError, match="same folder"):
        strip.strip_folder(clips, clips)
