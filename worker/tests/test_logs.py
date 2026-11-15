"""JSON log lines for Cloud Logging (vision_worker.logs)."""

import io
import json
import logging
from datetime import datetime

import pytest

from vision_worker import logs


def _logger(stream):
    logger = logging.getLogger(f"test.logs.{id(stream)}")
    logger.handlers[:] = []
    logger.propagate = False
    logger.setLevel(logging.DEBUG)
    handler = logging.StreamHandler(stream)
    handler.setFormatter(logs.JsonFormatter())
    logger.addHandler(handler)
    return logger


def _entries(stream):
    return [json.loads(line) for line in stream.getvalue().splitlines()]


@pytest.mark.parametrize(
    ("level", "severity"),
    [
        (logging.DEBUG, "DEBUG"),
        (logging.INFO, "INFO"),
        (logging.WARNING, "WARNING"),
        (logging.ERROR, "ERROR"),
        (logging.CRITICAL, "CRITICAL"),
    ],
)
def test_severity_uses_cloud_logging_names(level, severity):
    stream = io.StringIO()
    _logger(stream).log(level, "hello")
    assert _entries(stream)[0]["severity"] == severity


def test_one_entry_is_one_line_with_message_logger_and_time():
    stream = io.StringIO()
    logger = _logger(stream)
    logger.info("clip %s has %d frames\nsecond line", "squat_01", 120)

    lines = stream.getvalue().splitlines()
    assert len(lines) == 1
    entry = json.loads(lines[0])
    assert entry["message"] == "clip squat_01 has 120 frames\nsecond line"
    assert entry["logger"] == logger.name
    datetime.fromisoformat(entry["time"])


def test_exception_goes_in_stack_trace():
    stream = io.StringIO()
    logger = _logger(stream)
    try:
        raise ValueError("broken")
    except ValueError:
        logger.exception("failed")

    entry = _entries(stream)[0]
    assert entry["message"] == "failed"
    assert entry["severity"] == "ERROR"
    assert "ValueError: broken" in entry["stack_trace"]
    assert "test_exception_goes_in_stack_trace" in entry["stack_trace"]


def test_fields_given_with_extra_are_written():
    stream = io.StringIO()
    _logger(stream).info("loaded", extra={"fields": {"poseModel": "full", "seconds": 1.5}})
    assert _entries(stream)[0]["fields"] == {"poseModel": "full", "seconds": 1.5}


def test_missing_fields_are_left_out():
    stream = io.StringIO()
    _logger(stream).info("plain")
    entry = _entries(stream)[0]
    assert "fields" not in entry
    assert "stack_trace" not in entry


def test_configure_sends_the_root_logger_to_the_stream_as_json():
    stream = io.StringIO()
    root = logging.getLogger()
    saved = (root.handlers[:], root.level)
    try:
        logs.configure(stream=stream)
        logging.getLogger("vision_worker.anything").info("through the root")
    finally:
        root.handlers[:], level = saved
        root.setLevel(level)
    assert _entries(stream)[0]["message"] == "through the root"
