"""Protocol v2 request authentication (app/auth.py)."""

import json

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
