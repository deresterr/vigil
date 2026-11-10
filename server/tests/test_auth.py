"""Protocol v2 request authentication (app/auth.py)."""

import json
from datetime import UTC, datetime, timedelta

from app.db import get_connection
from tests.conftest import Watchdog

BODY = {"session_active": True, "user_active": True, "current_time_utc": "2026-11-04T11:00:00Z", "uptime_seconds": 1}


def _detail(r):
    return r.json()["detail"]


def test_signed_request_is_accepted(watchdog):
    assert watchdog.post("/v1/heartbeat", BODY).status_code == 200


def test_missing_signature_is_rejected(client, watchdog):
    raw = json.dumps(BODY).encode()
    headers = watchdog.headers("/v1/heartbeat", raw)
    del headers["X-Signature"]
    r = client.post("/v1/heartbeat", content=raw, headers=headers)
    assert r.status_code == 401 and _detail(r) == "missing_auth_headers"
    assert "X-Signature" not in r.headers  # rejections are never signed


def test_wrong_key_is_rejected(client):
    r = Watchdog(client, key=b"k" * 32).post("/v1/heartbeat", BODY, verify=False)
    assert r.status_code == 401 and _detail(r) == "invalid_signature"


def test_tampered_body_is_rejected(client, watchdog):
    raw = json.dumps(BODY).encode()
    headers = watchdog.headers("/v1/heartbeat", raw)
    tampered = json.dumps({**BODY, "session_active": False}).encode()
    r = client.post("/v1/heartbeat", content=tampered, headers=headers)
    assert r.status_code == 401 and _detail(r) == "invalid_signature"


def test_signature_is_bound_to_path(client, watchdog):
    raw = b"{}"
    headers = watchdog.headers("/v1/heartbeat", raw)
    r = client.post("/v1/pause_toggle", content=raw, headers=headers)
    assert r.status_code == 401 and _detail(r) == "invalid_signature"


def test_replayed_nonce_is_rejected(watchdog):
    assert watchdog.post("/v1/heartbeat", BODY, nonce="n-1").status_code == 200
    r = watchdog.post("/v1/heartbeat", BODY, verify=False, nonce="n-1")
    assert r.status_code == 401 and _detail(r) == "duplicate_nonce"


def test_old_timestamp_is_rejected(watchdog):
    r = watchdog.post("/v1/heartbeat", BODY, verify=False, ts="1000")
    assert r.status_code == 401 and _detail(r) == "timestamp_too_old"


def test_rejected_request_does_not_burn_nonce(client, watchdog):
    Watchdog(client, key=b"k" * 32).post("/v1/heartbeat", BODY, verify=False, nonce="n-2")
    assert watchdog.post("/v1/heartbeat", BODY, nonce="n-2").status_code == 200


def _server_ms(r) -> int:
    return int(r.headers["X-Server-Time"])


def test_every_v1_response_carries_the_server_time(client, watchdog):
    """Signed or not — a watchdog whose requests are rejected for their timestamp needs it most (#92)."""
    now_ms = int(datetime.now(UTC).timestamp() * 1000)
    for r in (watchdog.post("/v1/heartbeat", BODY),
              watchdog.post("/v1/heartbeat", BODY, verify=False, ts="1000"),
              client.post("/v1/pair", json={"code": "00000000"})):
        assert abs(_server_ms(r) - now_ms) < 5000, r.text
    assert "X-Server-Time" not in client.get("/health").headers


def test_a_watchdog_ahead_of_the_server_recovers_with_its_time(watchdog):
    """A PC clock an hour ahead (seen on a fresh VM): the first request is rejected, the next one is signed
    with the server's time from the rejection and passes. Its own clock still goes in current_time_utc,
    so the parent sees the drift in the Journal."""
    pc_now = datetime.now(UTC) + timedelta(hours=1)
    pc_ms = int(pc_now.timestamp() * 1000)
    rejected = watchdog.post("/v1/heartbeat", BODY, verify=False, ts=str(pc_ms))
    assert rejected.status_code == 401 and _detail(rejected) == "timestamp_in_future"

    offset_ms = _server_ms(rejected) - pc_ms
    body = {**BODY, "current_time_utc": pc_now.isoformat()}
    r = watchdog.post("/v1/heartbeat", body, ts=str(int(datetime.now(UTC).timestamp() * 1000) + 3_600_000 + offset_ms))
    assert r.status_code == 200
    with get_connection() as conn:
        drift = conn.execute("SELECT details_json FROM events WHERE event_type = 'clock_drift'").fetchone()
    assert drift is not None and json.loads(drift[0])["drift_seconds"] >= 3590  # PC ahead: positive
