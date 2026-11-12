"""Shared fixtures: fresh SQLite DB per test, one child with one paired device, a signed "watchdog".

Module-level state in the app (login throttle) is reset per test, otherwise values from a previous
test's DB leak into the next one.
"""

from __future__ import annotations

import base64
import hashlib
import hmac
import json
import secrets
from datetime import UTC, datetime

import pytest
from fastapi.testclient import TestClient

import app.admin_auth
import app.db
from app.config import week_schedule


@pytest.fixture
def client(tmp_path, monkeypatch):
    monkeypatch.setattr(app.db, "DATABASE_PATH", tmp_path / "vigil.db")
    app.admin_auth._login_failures.clear()
    from app import main as app_main
    from app.main import app as fastapi_app

    app_main._health_failing = False

    with TestClient(fastapi_app) as c:
        with app.db.get_connection() as conn:
            app.db.set_config_value(conn, "setup_complete", "1")
            app.db.set_config_value(conn, "timezone", "Europe/Warsaw")  # what the tests' local times assume
        add_child("Ola")
        add_device(1, "PC")
        yield c


def add_child(name: str, weekday: int = 120, weekend: int = 240, night: tuple[str, str] = ("21:30", "06:30")) -> int:
    """A child with the wizard's week shape: `weekday` limit Mon–Thu, `weekend` Fri–Sun, one night window."""
    with app.db.get_connection() as conn:
        child_id = conn.execute("INSERT INTO children(name, created_utc) VALUES (?, ?)",
                                (name, datetime.now(UTC).isoformat())).lastrowid
        week = week_schedule(weekday, weekend, *night)
        conn.executemany("INSERT INTO child_schedule VALUES (?, ?, ?, ?, ?)",
                         [(child_id, i, d["daily_limit"], d["night_start"], d["night_end"])
                          for i, d in enumerate(week)])
        return child_id


def add_device(child_id: int, name: str) -> int:
    """A paired device with a random key (Watchdog(client, device_id) signs with it)."""
    key = base64.urlsafe_b64encode(secrets.token_bytes(32)).rstrip(b"=").decode()
    with app.db.get_connection() as conn:
        return conn.execute(
            "INSERT INTO devices(child_id, name, signing_key, paired_utc) VALUES (?, ?, ?, ?)",
            (child_id, name, key, datetime.now(UTC).isoformat()),
        ).lastrowid


def device_key(device_id: int) -> bytes:
    with app.db.get_connection() as conn:
        key_b64 = conn.execute("SELECT signing_key FROM devices WHERE id = ?", (device_id,)).fetchone()[0]
    return base64.urlsafe_b64decode(key_b64 + "=" * (-len(key_b64) % 4))


def config(key: str) -> str:
    with app.db.get_connection() as conn:
        return app.db.get_config_value(conn, key)


def state_for(date_iso: str, child_id: int = 1) -> dict:
    with app.db.get_connection() as conn:
        row = conn.execute("SELECT * FROM child_daily WHERE child_id = ? AND date_local = ?",
                           (child_id, date_iso)).fetchone()
        return dict(row) if row else {}


def set_used(date_iso: str, minutes: int, child_id: int = 1) -> None:
    """The child's time used on that day (its child_daily row must exist: one heartbeat first)."""
    with app.db.get_connection() as conn:
        conn.execute("UPDATE child_daily SET seconds_used = ? WHERE child_id = ? AND date_local = ?",
                     (minutes * 60, child_id, date_iso))


def set_schedule(child_id: int = 1, weekdays=range(7), **fields) -> None:
    """Sets daily_limit / night_start / night_end of the given weekdays (Monday = 0; default all)."""
    with app.db.get_connection() as conn:
        for weekday in weekdays:
            conn.execute(f"UPDATE child_schedule SET {', '.join(f'{k} = ?' for k in fields)} "
                         "WHERE child_id = ? AND weekday = ?", (*fields.values(), child_id, weekday))


class Watchdog:
    """Speaks the /v1 protocol the way vigil_watchdog does (HeartbeatClient.cs)."""

    def __init__(self, client: TestClient, device_id: int = 1, key: bytes | None = None):
        self.c = client
        self.key = key if key is not None else device_key(device_id)

    def headers(self, path: str, body: bytes, nonce: str | None = None, ts: str | None = None) -> dict:
        nonce = nonce or base64.urlsafe_b64encode(secrets.token_bytes(16)).rstrip(b"=").decode()
        ts = ts or str(int(datetime.now(UTC).timestamp() * 1000))
        canonical = "\n".join(("POST", path, ts, nonce, hashlib.sha256(body).hexdigest())).encode()
        sig = base64.b64encode(hmac.new(self.key, canonical, hashlib.sha256).digest()).decode()
        return {"X-Nonce": nonce, "X-Timestamp": ts, "X-Signature": sig, "Content-Type": "application/json"}

    def post(self, path: str, body: dict, verify: bool = True, **header_overrides):
        raw = json.dumps(body).encode()
        headers = self.headers(path, raw, **header_overrides)
        r = self.c.post(path, content=raw, headers=headers)
        if verify:
            expected = hmac.new(self.key, headers["X-Nonce"].encode() + r.content, hashlib.sha256).digest()
            assert base64.b64decode(r.headers["X-Signature"]) == expected, "bad response signature"
        return r

    def heartbeat(self, active: bool = True, **telemetry) -> dict:
        r = self.post(
            "/v1/heartbeat",
            {
                "session_active": active,
                "user_active": active,
                "current_time_utc": datetime.now(UTC).isoformat(),
                "uptime_seconds": 100,  # still sent by the watchdog (servers up to 0.8.2 require it)
                **telemetry,
            },
        )
        assert r.status_code == 200, r.text
        return r.json()


@pytest.fixture
def watchdog(client) -> Watchdog:
    return Watchdog(client)
