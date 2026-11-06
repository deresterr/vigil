"""Shared fixtures: fresh SQLite DB per test + a signed "watchdog" client.

Module-level caches in the app (auth/heartbeat config) are reset per test, otherwise
values from a previous test's DB leak into the next one.
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

import app.auth
import app.db
import app.endpoints.heartbeat


@pytest.fixture
def client(tmp_path, monkeypatch):
    monkeypatch.setattr(app.db, "DATABASE_PATH", tmp_path / "vigil.db")
    app.auth._auth_cfg = None
    app.endpoints.heartbeat._hb_cfg = None
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

    def __init__(self, client: TestClient):
        self.c = client
        self.bearer = config("bearer_token")
        key_b64 = config("signing_key")
        self.key = base64.urlsafe_b64decode(key_b64 + "=" * (-len(key_b64) % 4))

    def post(self, path: str, body: dict, verify: bool = True):
        nonce = base64.urlsafe_b64encode(secrets.token_bytes(16)).rstrip(b"=").decode()
        ts = str(int(datetime.now(UTC).timestamp() * 1000))
        r = self.c.post(
            path,
            content=json.dumps(body),
            headers={
                "Authorization": f"Bearer {self.bearer}",
                "X-Nonce": nonce,
                "X-Timestamp": ts,
                "Content-Type": "application/json",
            },
        )
        if verify:
            expected = hmac.new(self.key, nonce.encode() + r.content, hashlib.sha256).digest()
            assert base64.b64decode(r.headers["X-Signature"]) == expected, "bad response signature"
        return r

    def heartbeat(self, active: bool = True) -> dict:
        r = self.post(
            "/v1/heartbeat",
            {
                "session_active": active,
                "user_active": active,
                "current_time_utc": datetime.now(UTC).isoformat(),
                "uptime_seconds": 100,
            },
        )
        assert r.status_code == 200, r.text
        return r.json()


@pytest.fixture
def watchdog(client) -> Watchdog:
    return Watchdog(client)
