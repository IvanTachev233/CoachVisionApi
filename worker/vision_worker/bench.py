"""How long the worker takes per clip, against the bar of twice the clip's length (VS3, bar 3).

    python -m vision_worker.bench /path/to/clips [--proposer-dir DIR] [--lift squat]

Runs the worker's command line on every clip in the folder, one after another, with the Full pose
model and the default plate finder, as a job would. Each clip is first copied to a local folder (the
copy is timed apart), so a clip read over the network doesn't count as analysis. One JSON log line
per clip, then a table, go to standard output; nothing is written beside the clips.

Made to run in the worker image on Cloud Run with 2 vCPU (V1c): the image has the Full model,
`--proposer-dir` names the folder with the plate proposer's file. The log names how many processors
the machine reports and the container's CPU quota, so the figures can be read against both.
"""

import argparse
import json
import logging
import os
import shutil
import statistics
import sys
import tempfile
import time
from dataclasses import dataclass
from pathlib import Path

from vision_worker import cli, logs, models

CLIP_SUFFIXES = {".mp4", ".mov", ".m4v"}
BAR = 2.0  # at most twice the clip's length (VS3)
CGROUP_CPU_MAX = Path("/sys/fs/cgroup/cpu.max")

logger = logging.getLogger(__name__)


class BenchError(Exception):
    """The run can't start: no clips, or a model file is missing."""


@dataclass
class Row:
    clip: str
    length_s: float | None
    analysis_s: float
    copy_s: float
    failed: bool = False

    @property
    def ratio(self):
        if self.failed or not self.length_s:
            return None
        return self.analysis_s / self.length_s

    @property
    def within_bar(self):
        return self.ratio is not None and self.ratio <= BAR


def clips_in(folder):
    return sorted(p for p in Path(folder).iterdir() if p.is_file() and p.suffix.lower() in CLIP_SUFFIXES)


def measure(folder, run=cli.main, clock=time.perf_counter, work_dir=None, lift=None, model_dir=None):
    """Times the worker on each clip in `folder`; returns one Row per clip, in name order."""
    clips = clips_in(folder)
    if not clips:
        raise BenchError(f"no clips ({', '.join(sorted(CLIP_SUFFIXES))}) in {folder}")
    work = Path(work_dir or tempfile.mkdtemp(prefix="vision-bench-"))
    work.mkdir(parents=True, exist_ok=True)

    rows = []
    for clip in clips:
        local = work / clip.name
        out = work / f"{clip.name}.json"
        started = clock()
        shutil.copyfile(clip, local)
        copy_s = clock() - started

        argv = [str(local), "--out", str(out), "--model", "full"]
        if lift:
            argv += ["--lift", lift]
        if model_dir:
            argv += ["--model-dir", str(model_dir)]
        started = clock()
        try:
            code = run(argv)
        except Exception:
            logger.exception("worker crashed", extra={"fields": {"clip": clip.name}})
            code = 1
        analysis_s = clock() - started

        length_s = None
        if code == 0:
            length_s = json.loads(out.read_text())["video"]["durationS"]
        row = Row(clip.name, length_s, analysis_s, copy_s, failed=code != 0)
        rows.append(row)
        local.unlink(missing_ok=True)
        out.unlink(missing_ok=True)
        logger.info(
            "clip timed",
            extra={
                "fields": {
                    "clip": row.clip,
                    "lengthS": row.length_s,
                    "analysisS": round(row.analysis_s, 2),
                    "copyS": round(row.copy_s, 2),
                    "ratio": None if row.ratio is None else round(row.ratio, 3),
                    "withinBar": row.within_bar,
                    "failed": row.failed,
                }
            },
        )
    return rows


def _number(value, places):
    return "–" if value is None else f"{value:.{places}f}"


def table(rows):
    """A Markdown table: a row per clip, then how many clips are within the bar."""
    lines = [
        "| Clip | Length (s) | Analysis (s) | × length | Within 2× |",
        "| --- | ---: | ---: | ---: | --- |",
    ]
    for row in rows:
        verdict = "failed" if row.failed else ("yes" if row.within_bar else "no")
        lines.append(
            f"| {row.clip} | {_number(row.length_s, 1)} | {row.analysis_s:.1f} | {_number(row.ratio, 2)} | {verdict} |"
        )
    ratios = [row.ratio for row in rows if row.ratio is not None]
    summary = f"Within 2× the clip's length: {sum(row.within_bar for row in rows)} of {len(rows)} clips"
    if ratios:
        summary += f"; median {statistics.median(ratios):.2f}×, worst {max(ratios):.2f}×"
    return "\n".join(lines) + "\n\n" + summary + ".\n"


def cpu_report(cpu_max=CGROUP_CPU_MAX):
    """Processors the system reports, those this process may use, and the container's CPU quota."""
    quota = None
    try:
        limit, period = Path(cpu_max).read_text().split()
        if limit != "max":
            quota = int(limit) / int(period)
    except (OSError, ValueError):
        pass
    affinity = len(os.sched_getaffinity(0)) if hasattr(os, "sched_getaffinity") else None
    return {"cpuCount": os.cpu_count(), "affinity": affinity, "cgroupCpus": quota}


def _model_dir_with_proposer(proposer_dir):
    """A folder holding the image's Full pose model and the proposer from `proposer_dir`, both checked."""
    folder = Path(tempfile.mkdtemp(prefix="vision-models-"))
    (folder / models.MODEL_FILES["full"]).symlink_to(models.model_path("full"))
    (folder / models.PROPOSER_FILE).symlink_to(models.proposer_path(proposer_dir))
    return folder


def main(argv=None):
    parser = argparse.ArgumentParser(prog="vision_worker.bench", description=__doc__.split("\n\n")[0])
    parser.add_argument("clips", help="folder with the clips")
    parser.add_argument("--proposer-dir", help="folder with the plate proposer's file, if not with the pose model")
    parser.add_argument("--lift", default="squat", choices=cli.LIFTS, help="lift passed to the worker (default: squat)")
    parser.add_argument("--work-dir", help="local folder for the clip copies (default: a new temporary folder)")
    args = parser.parse_args(argv)

    logs.configure()
    try:
        model_dir = _model_dir_with_proposer(args.proposer_dir) if args.proposer_dir else None
        logger.info("bench started", extra={"fields": cpu_report()})
        rows = measure(args.clips, work_dir=args.work_dir, lift=args.lift, model_dir=model_dir)
    except (BenchError, models.ModelError) as error:
        logger.error(str(error))
        return 1
    logger.info(table(rows), extra={"fields": {"withinBar": sum(r.within_bar for r in rows), "clips": len(rows)}})
    return 0


if __name__ == "__main__":
    sys.exit(main())
