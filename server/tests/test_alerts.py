"""Parent-panel alerts (#8)."""

from app.crypto import hash_password
from app.db import get_connection, log_event, set_config_value

PASSWORD = "haslo-rodzica"


def _login(client):
    with get_connection() as conn:
        set_config_value(conn, "parent_password_hash", hash_password(PASSWORD))
    client.post("/admin/login", data={"password": PASSWORD})


def _log(*types):
    with get_connection() as conn:
        for t in types:
            log_event(conn, t, {"down_awake_seconds": 60} if t.startswith("tamper_") else None)


def test_only_alert_types_are_listed(client):
    _login(client)
    _log("tamper_watchdog_restarted", "watchdog_started", "totp_failed", "totp_lockout", "tamperproof_lookalike")
    assert client.get("/admin/api/status").json()["alerts_unacked"] == 2
    alerts = client.get("/admin/api/alerts").json()["alerts"]
    assert [a["type"] for a in alerts] == ["totp_lockout", "tamper_watchdog_restarted"]  # newest first
    assert alerts[1]["details"] == {"down_awake_seconds": 60}
    assert alerts[0]["ts_local"]


def test_ack_selected_then_all(client):
    _login(client)
    _log("tamper_service_stopped", "tamper_watchdog_silent", "admin_login_locked")
    ids = [a["id"] for a in client.get("/admin/api/alerts").json()["alerts"]]
    r = client.post("/admin/api/alerts/ack", json={"ids": ids[:1]})
    assert r.json() == {"ok": True, "acked": 1}
    assert client.get("/admin/api/status").json()["alerts_unacked"] == 2
    assert client.post("/admin/api/alerts/ack", json={}).json()["acked"] == 2
    assert client.get("/admin/api/alerts").json()["alerts"] == []


def test_ack_ignores_non_alert_ids(client):
    _login(client)
    _log("watchdog_started")
    with get_connection() as conn:
        eid = conn.execute("SELECT id FROM events WHERE event_type = 'watchdog_started'").fetchone()[0]
    assert client.post("/admin/api/alerts/ack", json={"ids": [eid]}).json()["acked"] == 0


def test_ack_rejects_bad_ids(client):
    _login(client)
    assert client.post("/admin/api/alerts/ack", json={"ids": ["1"]}).status_code == 422


def test_alerts_require_admin_session(client):
    assert client.get("/admin/api/alerts").status_code == 401
