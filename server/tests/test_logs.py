"""Container log (#100): one line format, no access log, the server's state and errors only."""

import logging
import sqlite3

import pytest

import app.db
import app.endpoints.admin_views
import app.logs
from app.main import app as fastapi_app


def _server_records(caplog, when: str = "call") -> list[logging.LogRecord]:
    """The server's records; the test client logs its own requests (httpx)."""
    return [r for r in caplog.get_records(when) if not r.name.startswith("httpx")]


def _record(level=logging.INFO, msg="Hello %s", args=("world",), exc_info=None) -> logging.LogRecord:
    return logging.LogRecord("vigil", level, __file__, 1, msg, args, exc_info)


def test_line_format_without_color():
    line = app.logs.LineFormatter(color=False).format(_record(logging.WARNING))
    date, time, level, message = line.split(" ", 3)
    assert len(date) == 10 and len(time) == 8
    assert level == "WARN"
    assert message == " Hello world"  # level padded to 5 characters


def test_line_format_colors_the_level():
    line = app.logs.LineFormatter(color=True).format(_record(logging.ERROR))
    assert "\033[1;31mERROR\033[0m Hello world" in line
    assert line.startswith("\033[90m")


def test_traceback_follows_the_line():
    try:
        raise RuntimeError("boom")
    except RuntimeError:
        import sys
        rec = _record(logging.ERROR, "Failed", (), sys.exc_info())
    first, *rest = app.logs.LineFormatter(color=False).format(rec).splitlines()
    assert first.endswith("ERROR Failed")
    assert rest[0] == "Traceback (most recent call last):"
    assert rest[-1] == "RuntimeError: boom"


@pytest.mark.parametrize(("value", "enabled"), [(None, True), ("", True), ("1", False)])
def test_no_color(monkeypatch, value, enabled):
    if value is None:
        monkeypatch.delenv("NO_COLOR", raising=False)
    else:
        monkeypatch.setenv("NO_COLOR", value)
    assert app.logs.color_enabled() is enabled


def test_uvicorn_goes_through_the_app_format_without_access_log():
    app.logs.setup_logging()  # again: idempotent
    root = logging.getLogger()
    assert sum(isinstance(h.formatter, app.logs.LineFormatter) for h in root.handlers) == 1
    assert logging.getLogger("uvicorn.access").disabled
    assert logging.getLogger("uvicorn.error").getEffectiveLevel() == logging.WARNING
    for name in ("uvicorn", "uvicorn.error"):
        assert logging.getLogger(name).handlers == []
        assert logging.getLogger(name).propagate


def test_startup_line_and_setup_warning(caplog, client):
    lines = [(r.levelname, r.getMessage()) for r in _server_records(caplog, "setup")]
    assert lines[0][0] == "INFO"
    assert lines[0][1].startswith(f"Vigil {app.logs.VERSION} started")
    assert f"{app.db.DATABASE_PATH} (new, schema " in lines[0][1]
    # the fixture completes the wizard only after the start
    assert ("WARNING", "Setup not completed: open /setup to finish it") in lines


def test_panel_requests_are_not_logged(caplog, client):
    client.get("/admin/login")
    client.get("/favicon.ico")
    assert _server_records(caplog) == []


def test_unhandled_error_names_the_request(caplog, client, monkeypatch):
    def broken(_name):
        raise RuntimeError("boom")

    monkeypatch.setattr(app.endpoints.admin_views, "page", broken)
    r = client.get("/admin/login")
    assert r.status_code == 500
    assert r.text == "Internal Server Error"
    [rec] = _server_records(caplog)
    assert rec.levelno == logging.ERROR
    assert rec.getMessage() == "Unhandled error in GET /admin/login"
    assert rec.exc_info[0] is RuntimeError
    text = app.logs.LineFormatter(color=False).format(rec)
    assert text.count("Traceback (most recent call last):") == 1  # not again inside an exception group
    assert text.endswith("RuntimeError: boom")


def test_health_failure_logged_once_then_recovery(caplog, client):
    class Broken:
        def execute(self, *_):
            raise sqlite3.OperationalError("disk I/O error")

    async def broken_db():
        yield Broken()

    fastapi_app.dependency_overrides[app.db.get_db] = broken_db
    try:
        assert client.get("/health").status_code == 503
        assert client.get("/health").status_code == 503
    finally:
        fastapi_app.dependency_overrides.clear()
    assert client.get("/health").status_code == 200
    assert client.get("/health").status_code == 200

    assert [(r.levelname, r.getMessage()) for r in _server_records(caplog)] == [
        ("ERROR", "Health check: database not responding"),
        ("INFO", "Health check: database OK again"),
    ]
