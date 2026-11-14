"""Log lines as JSON for Cloud Logging, which reads one JSON object per line from standard output.

Each line has `severity` in Cloud Logging's names, `message`, `time`, the logger's name, the values
passed as `extra={"fields": {...}}`, and an exception under `stack_trace`. The same shape as the API's.
"""

import json
import logging
import sys
from datetime import UTC, datetime

_SEVERITY = {
    logging.DEBUG: "DEBUG",
    logging.INFO: "INFO",
    logging.WARNING: "WARNING",
    logging.ERROR: "ERROR",
    logging.CRITICAL: "CRITICAL",
}


class JsonFormatter(logging.Formatter):
    def format(self, record):
        entry = {
            "severity": _SEVERITY.get(record.levelno, "DEFAULT"),
            "message": record.getMessage(),
            "time": datetime.fromtimestamp(record.created, UTC).isoformat(),
            "logger": record.name,
        }
        fields = getattr(record, "fields", None)
        if fields:
            entry["fields"] = fields
        if record.exc_info:
            entry["stack_trace"] = self.formatException(record.exc_info)
        return json.dumps(entry, ensure_ascii=False, default=str)


def configure(level=logging.INFO, stream=None):
    """Send every log record, the web server's included, to `stream` (standard output) as JSON."""
    handler = logging.StreamHandler(stream or sys.stdout)
    handler.setFormatter(JsonFormatter())
    root = logging.getLogger()
    root.handlers[:] = [handler]
    root.setLevel(level)
