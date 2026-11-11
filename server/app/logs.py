"""Container log (#100): the server's own state and its errors, one line format for the app and uvicorn.

What happens on the children's PCs is the panel journal (`events`), not the log; HTTP requests are not logged
(the panel's polling and a watchdog with a revoked key every 5 s would flood it). Unhandled errors keep their
traceback.

    2026-01-05 18:00:00 INFO  Vigil 0.9.0 started · port 8088 · /data/vigil.db (schema 2)

The level is colored with ANSI codes. A container has no TTY to detect, so colors are on unless NO_COLOR is set
to a non-empty value (no-color.org), e.g. when the log goes to a file.
"""

from __future__ import annotations

import logging
import os
import sys

# Set by the release workflow (--build-arg VIGIL_VERSION, Dockerfile ENV); a local build is "dev".
VERSION = os.environ.get("VIGIL_VERSION") or "dev"

_RESET = "\033[0m"
_GRAY = "\033[90m"
_LEVELS = {  # name (5 characters at most, so messages line up), color
    logging.DEBUG: ("DEBUG", "\033[2m"),
    logging.INFO: ("INFO", "\033[32m"),
    logging.WARNING: ("WARN", "\033[33m"),
    logging.ERROR: ("ERROR", "\033[1;31m"),
    logging.CRITICAL: ("CRIT", "\033[1;31m"),
}


def color_enabled() -> bool:
    return not os.environ.get("NO_COLOR")


class LineFormatter(logging.Formatter):
    """`date time LEVEL message`, the traceback below it. No logger name: the message says what it is about."""

    def __init__(self, color: bool) -> None:
        super().__init__(datefmt="%Y-%m-%d %H:%M:%S")
        self.color = color

    def format(self, record: logging.LogRecord) -> str:
        ts = self.formatTime(record, self.datefmt)
        name, code = _LEVELS.get(record.levelno, (record.levelname[:5], ""))
        if self.color:
            line = f"{_GRAY}{ts}{_RESET} {code}{name:<5}{_RESET} {record.getMessage()}"
        else:
            line = f"{ts} {name:<5} {record.getMessage()}"
        if record.exc_info:
            line += "\n" + self.formatException(record.exc_info)
        if record.stack_info:
            line += "\n" + self.formatStack(record.stack_info)
        return line


def setup_logging() -> None:
    """Called when app.main is imported, which uvicorn does after its own logging setup, so this wins.

    Every logger goes through one handler on the root logger. uvicorn keeps its warnings and errors
    (`Invalid HTTP request received`, a failed bind, a failed startup); its INFO lines (process started,
    waiting for startup, running on …, shutting down) are replaced by the lifespan's "started" and "stopped".
    """
    root = logging.getLogger()
    if not any(isinstance(h.formatter, LineFormatter) for h in root.handlers):
        handler = logging.StreamHandler(sys.stderr)
        handler.setFormatter(LineFormatter(color_enabled()))
        root.addHandler(handler)
    root.setLevel(logging.INFO)

    for name in ("uvicorn", "uvicorn.error", "uvicorn.access"):
        lg = logging.getLogger(name)
        lg.handlers.clear()
        lg.propagate = True
    logging.getLogger("uvicorn.error").setLevel(logging.WARNING)
    logging.getLogger("uvicorn.access").disabled = True
