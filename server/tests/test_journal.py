"""Journal tab + alerts (#8)."""

from datetime import datetime, timedelta
from zoneinfo import ZoneInfo

import pyotp
import time_machine

from app.crypto import hash_password
from app.db import get_connection, log_event, set_config_value
from tests.conftest import config

TZ = ZoneInfo("Europe/Warsaw")
PASSWORD = "haslo-rodzica"


def _login(client):
    with get_connection() as conn:
        set_config_value(conn, "parent_password_hash", hash_password(PASSWORD))
    client.post("/admin/login", data={"password": PASSWORD})


def _log(etype, details=None):
    with get_connection() as conn:
        log_event(conn, etype, details)


def _today(client):
    return client.get("/admin/api/journal").json()["days"][0]


def test_journal_shows_only_relevant_types_newest_first(client):
    _login(client)
    _log("watchdog_started", {"same_boot": False})
    _log("admin_login_success")        # parent's own panel activity: hidden
    _log("config_updated", {"keys": ["x"]})
    _log("grant_applied", {"mode": "extend_limit", "minutes": 30})
    _log("tamper_watchdog_restarted", {"down_awake_seconds": 120})
    types = [e["type"] for e in _today(client)["entries"]]
    assert types == ["tamper_watchdog_restarted", "grant_applied", "watchdog_started"]


def test_pause_is_one_entry_with_duration(client):
    with time_machine.travel(datetime(2026, 11, 4, 19, 40, tzinfo=TZ), tick=False) as t:
        _login(client)
        _log("pause_toggled", {"pause_active": True})
        t.shift(timedelta(minutes=12))
        _log("pause_toggled", {"pause_active": False})
        t.shift(timedelta(minutes=5))
        _log("pause_toggled", {"pause_active": True})  # still paused
        entries = _today(client)["entries"]
    assert [(e["type"], e["details"]) for e in entries] == [
        ("pause", {"from": "19:57", "to": None}),
        ("pause", {"from": "19:40", "to": "19:52", "minutes": 12}),
    ]


def test_shutdown_carries_session_length(client):
    with time_machine.travel(datetime(2026, 11, 4, 16, 5, tzinfo=TZ), tick=False) as t:
        _login(client)
        _log("watchdog_started", {"same_boot": False})
        t.shift(timedelta(hours=5, minutes=37))
        _log("watchdog_shutdown", {"instance_id": "a"})
        entry = _today(client)["entries"][0]
    assert entry["type"] == "watchdog_shutdown"
    assert entry["details"]["on_since"] == "16:05" and entry["details"]["minutes"] == 337


def test_day_summary_and_paging(client, watchdog):
    with time_machine.travel(datetime(2026, 11, 6, 12, 0, tzinfo=TZ), tick=False):
        _login(client)
        watchdog.heartbeat()
        r = client.get("/admin/api/journal?days=2").json()
        assert [d["date"] for d in r["days"]] == ["2026-11-06", "2026-11-05"]
        assert r["days"][0]["children"][0]["limit_minutes"] == 240  # Friday counts as weekend
        older = client.get("/admin/api/journal?before=2026-11-05&days=1").json()
        assert [d["date"] for d in older["days"]] == ["2026-11-04"]
        assert client.get("/admin/api/journal?days=99").status_code == 422


def test_lock_start_is_logged_once(client, watchdog):
    with time_machine.travel(datetime(2026, 11, 4, 21, 0, tzinfo=TZ), tick=False) as t:
        _login(client)
        watchdog.heartbeat()
        t.shift(timedelta(minutes=31))  # 21:31, night window started at 21:30
        watchdog.heartbeat()
        t.shift(5)
        watchdog.heartbeat()
        entries = _today(client)["entries"]
    assert [(e["type"], e["details"]["reason"]) for e in entries] == [("lock_started", "night")]


def test_totp_grant_after_limit_reads_as_story(client, watchdog):
    with time_machine.travel(datetime(2026, 11, 4, 12, 0, tzinfo=TZ), tick=False) as t:
        _login(client)
        watchdog.heartbeat()
        with get_connection() as conn:
            conn.execute("UPDATE child_daily SET seconds_used = ? WHERE child_id = 1 AND date_local = ?",
                         (120 * 60 - 5, "2026-11-04"))
        t.shift(5)
        watchdog.heartbeat()  # limit reached
        watchdog.post("/v1/totp_attempt", {"code": pyotp.TOTP(config("totp_secret")).now(), "minutes_to_grant": 30})
        entries = _today(client)["entries"]
    assert [e["type"] for e in entries] == ["grant_applied", "lock_started"]


def test_alert_count_and_ack(client):
    _login(client)
    _log("tamper_service_stopped", {"instance_id": "a"})
    _log("totp_lockout", {"seconds": 60})
    _log("watchdog_started", {"same_boot": True})
    assert client.get("/admin/api/status").json()["alerts_unacked"] == 2
    entries = _today(client)["entries"]
    alert_ids = [e["id"] for e in entries if e["alert"]]
    assert client.post("/admin/api/alerts/ack", json={"ids": alert_ids[:1]}).json() == {"ok": True, "acked": 1}
    assert client.get("/admin/api/status").json()["alerts_unacked"] == 1
    assert client.post("/admin/api/alerts/ack", json={}).json()["acked"] == 1
    assert all(e["acked"] for e in _today(client)["entries"] if e["alert"])


def test_ack_ignores_non_alerts_and_rejects_bad_ids(client):
    _login(client)
    _log("watchdog_started", {"same_boot": False})
    eid = _today(client)["entries"][0]["id"]
    assert client.post("/admin/api/alerts/ack", json={"ids": [eid]}).json()["acked"] == 0
    assert client.post("/admin/api/alerts/ack", json={"ids": ["1"]}).status_code == 422


def test_journal_requires_admin_session(client):
    assert client.get("/admin/api/journal").status_code == 401


def test_entries_are_ordered_by_event_time_not_arrival(client):
    """Offline unlocks and outages reach the server on reconnect; the journal must still read in order."""
    with time_machine.travel(datetime(2026, 11, 4, 0, 45, tzinfo=TZ), tick=False):
        _login(client)
        with get_connection() as conn:
            log_event(conn, "watchdog_offline", {"awake_gap_seconds": 180},
                      ts_utc=datetime(2026, 11, 4, 0, 39, tzinfo=TZ))
            log_event(conn, "offline_unlock", {"minutes": 60, "step": 1},
                      ts_utc=datetime(2026, 11, 4, 0, 41, tzinfo=TZ))
            log_event(conn, "watchdog_started", {"same_boot": False},
                      ts_utc=datetime(2026, 11, 4, 0, 34, tzinfo=TZ))
        entries = _today(client)["entries"]
    assert [(e["time"], e["type"]) for e in entries] == [
        ("00:41", "offline_unlock"), ("00:39", "watchdog_offline"), ("00:34", "watchdog_started")]


def test_clock_drift_once_a_day_in_the_journal(client, watchdog):
    """The watchdog signs with the server's time but reports its own clock (#92): an hour ahead is one Journal
    entry per day, not one per heartbeat."""
    noon = datetime(2026, 11, 4, 12, 0, tzinfo=TZ)
    with time_machine.travel(noon, tick=False) as t:
        _login(client)  # inside: a session issued "in the future" is refused
        for _ in range(3):
            watchdog.heartbeat(current_time_utc=(datetime.now(TZ) + timedelta(hours=1)).isoformat())
            t.shift(5)
        watchdog.heartbeat()  # a clock set right logs nothing
        entries = [e for e in _today(client)["entries"] if e["type"] == "clock_drift"]
        assert len(entries) == 1 and entries[0]["details"]["drift_seconds"] >= 3595
        t.shift(timedelta(days=1))
        _login(client)  # the 24 h session has run out
        watchdog.heartbeat(current_time_utc=(datetime.now(TZ) - timedelta(minutes=10)).isoformat())
        drifts = [e["details"]["drift_seconds"] for e in _today(client)["entries"] if e["type"] == "clock_drift"]
        assert drifts == [-600]
