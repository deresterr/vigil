"""Shared fixtures: fresh SQLite DB per test + a signed "watchdog" client.

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


@pytest.fixture
def client(tmp_path, monkeypatch):
    monkeypatch.setattr(app.db, "DATABASE_PATH", tmp_path / "vigil.db")
    app.admin_auth._login_failures.clear()
    from app.main import app as fastapi_app

    with TestClient(fastapi_app) as c:
        with app.db.get_connection() as conn:
            app.db.set_config_value(conn, "setup_complete", "1")
        yield c


def config(key: str) -> str:
    with app.db.get_connection() as conn:
        return app.db.get_config_value(conn, key)


def state_for(date_iso: str) -> dict:
    with app.db.get_connection() as conn:
        row = conn.execute("SELECT * FROM daily_state WHERE date_local = ?", (date_iso,)).fetchone()
        return dict(row) if row else {}


class Watchdog:
    """Speaks the /v1 protocol the way vigil_watchdog does (HeartbeatClient.cs)."""

    def __init__(self, client: TestClient, key: bytes | None = None):
        self.c = client
        if key is None:
            key_b64 = config("signing_key")
            key = base64.urlsafe_b64decode(key_b64 + "=" * (-len(key_b64) % 4))
        self.key = key

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
                "uptime_seconds": 100,
                **telemetry,
            },
        )
        assert r.status_code == 200, r.text
        return r.json()


@pytest.fixture
def watchdog(client) -> Watchdog:
    return Watchdog(client)
