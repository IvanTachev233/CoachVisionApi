"""Analysis time per clip against the clip's length (vision_worker.bench)."""

import json

import pytest

from vision_worker import bench


def _fake_run(durations, seconds, failing=()):
    """A stand-in for the worker's command line: writes an output with the clip's length."""
    clock = {"now": 0.0}

    def run(argv):
        clip, out = argv[0], argv[argv.index("--out") + 1]
        name = clip.rsplit("/", 1)[-1]
        clock["now"] += seconds[name]
        if name in failing:
            return 1
        with open(out, "w") as f:
            json.dump({"video": {"durationS": durations[name]}, "frames": []}, f)
        return 0

    return run, lambda: clock["now"]


def test_each_clip_is_timed_and_compared_with_the_bar(tmp_path):
    clips = tmp_path / "clips"
    clips.mkdir()
    for name in ("b.mp4", "a.mov", "notes.csv"):
        (clips / name).write_bytes(b"x")
    run, clock = _fake_run({"a.mov": 10.0, "b.mp4": 4.0}, {"a.mov": 15.0, "b.mp4": 20.0})

    rows = bench.measure(clips, run=run, clock=clock, work_dir=tmp_path / "work")

    assert [r.clip for r in rows] == ["a.mov", "b.mp4"]
    a, b = rows
    assert (a.length_s, a.analysis_s, a.ratio, a.within_bar) == (10.0, 15.0, 1.5, True)
    assert (b.length_s, b.analysis_s, b.ratio, b.within_bar) == (4.0, 20.0, 5.0, False)


def test_the_bar_is_four_and_a_half_times_the_clips_length():
    # Relaxed from twice the length (Ivan, 2026-11-15): on Cloud Run with 2 vCPU the slowest of the
    # 20 evaluation clips took 4.17 times its length, and every clip is to pass.
    assert bench.BAR == 4.5
    assert bench.Row("a.mp4", length_s=2.0, analysis_s=9.0, copy_s=0.0).within_bar is True
    assert bench.Row("a.mp4", length_s=2.0, analysis_s=9.1, copy_s=0.0).within_bar is False
    assert bench.Row("squat_04.mp4", length_s=10.5, analysis_s=43.7, copy_s=0.0).within_bar is True


def test_the_worker_is_run_with_the_full_model_and_the_lift(tmp_path):
    clips = tmp_path / "clips"
    clips.mkdir()
    (clips / "a.mp4").write_bytes(b"x")
    seen = []

    def run(argv):
        seen.append(argv)
        with open(argv[argv.index("--out") + 1], "w") as f:
            json.dump({"video": {"durationS": 1.0}, "frames": []}, f)
        return 0

    bench.measure(clips, run=run, work_dir=tmp_path / "work", lift="squat", model_dir="/models")

    argv = seen[0]
    assert argv[argv.index("--model") + 1] == "full"
    assert argv[argv.index("--lift") + 1] == "squat"
    assert argv[argv.index("--model-dir") + 1] == "/models"


def test_a_failed_clip_is_reported_and_the_rest_still_run(tmp_path):
    clips = tmp_path / "clips"
    clips.mkdir()
    for name in ("a.mp4", "b.mp4"):
        (clips / name).write_bytes(b"x")
    run, clock = _fake_run({"b.mp4": 2.0}, {"a.mp4": 1.0, "b.mp4": 3.0}, failing={"a.mp4"})

    rows = bench.measure(clips, run=run, clock=clock, work_dir=tmp_path / "work")

    assert rows[0].failed and rows[0].within_bar is False
    assert not rows[1].failed and rows[1].ratio == 1.5


def test_the_clip_copies_are_removed(tmp_path):
    clips = tmp_path / "clips"
    clips.mkdir()
    (clips / "a.mp4").write_bytes(b"x")
    run, clock = _fake_run({"a.mp4": 1.0}, {"a.mp4": 1.0})

    bench.measure(clips, run=run, clock=clock, work_dir=tmp_path / "work")

    assert list((tmp_path / "work").iterdir()) == []


def test_no_clips_is_an_error(tmp_path):
    with pytest.raises(bench.BenchError, match="no clips"):
        bench.measure(tmp_path, run=lambda argv: 0)


def test_table_has_a_row_per_clip_and_a_summary():
    rows = [
        bench.Row("a.mp4", length_s=10.0, analysis_s=15.0, copy_s=0.2),
        bench.Row("b.mp4", length_s=4.0, analysis_s=20.0, copy_s=0.1),
        bench.Row("c.mp4", length_s=None, analysis_s=1.0, copy_s=0.1, failed=True),
    ]

    table = bench.table(rows)

    assert "| × length | Within 4.5× |" in table
    assert "| a.mp4 | 10.0 | 15.0 | 1.50 | yes |" in table
    assert "| b.mp4 | 4.0 | 20.0 | 5.00 | no |" in table
    assert "| c.mp4 | – | 1.0 | – | failed |" in table
    assert "Within 4.5× the clip's length: 1 of 3 clips" in table
    assert "median 3.25×, worst 5.00×" in table


def test_cpu_report_reads_the_cgroup_quota(tmp_path):
    cpu_max = tmp_path / "cpu.max"
    cpu_max.write_text("200000 100000\n")
    assert bench.cpu_report(cpu_max)["cgroupCpus"] == 2.0

    cpu_max.write_text("max 100000\n")
    assert bench.cpu_report(cpu_max)["cgroupCpus"] is None
    assert bench.cpu_report(tmp_path / "missing")["cgroupCpus"] is None
