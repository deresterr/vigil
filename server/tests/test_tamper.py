"""Tamper detection from watchdog telemetry (app/tamper.py, issue #7)."""

import json
from datetime import datetime, timedelta, timezone

import time_machine

from app.crypto import hash_password
from app.db import get_connection, set_config_value
from tests.conftest import Watchdog


def _events(prefix: str = "") -> list[tuple[str, dict]]:
    with get_connection() as conn:
        rows = conn.execute(
            "SELECT event_type, details_json FROM events WHERE event_type LIKE ? ORDER BY id", (prefix + "%",)
        ).fetchall()
    return [(r[0], json.loads(r[1]) if r[1] else {}) for r in rows]


def _wd_events():
    return [e for e in _events() if e[0].startswith(("tamper_", "watchdog_"))]


class Pc:
    """Simulates one PC: an awake clock and the currently running watchdog process."""

    def __init__(self, wd: Watchdog):
        self.wd, self.awake, self.n, self.failed = wd, 600_000, 0, 0
        self.start()

    def start(self):
        self.n += 1
        self.instance, self.started = f"inst-{self.n}", self.awake

    def tick(self, seconds: float = 5, failed: int = 0):
        self.awake += int(seconds * 1000)
        return self.wd.heartbeat(instance_id=self.instance, awake_ms=self.awake,
                                 awake_ms_at_start=self.started, failed_attempts=failed)

    def goodbye(self, reason: str):
        return self.wd.post("/v1/goodbye", {"instance_id": self.instance, "reason": reason})

    def reboot(self):
        self.awake = 30_000
        self.start()


def test_steady_heartbeats_raise_nothing(watchdog):
    pc = Pc(watchdog)
    for _ in range(10):
        pc.tick()
    assert _wd_events() == []


def test_restart_within_same_boot_without_goodbye_is_tamper(watchdog):
    pc = Pc(watchdog)
    pc.tick()
    pc.awake += 120_000  # killed, recovery restarted it two awake-minutes later
    pc.start()
    pc.tick()
    assert _wd_events() == [("tamper_watchdog_restarted", {"down_awake_seconds": 120})]


def test_reboot_is_not_tamper(watchdog):
    pc = Pc(watchdog)
    pc.tick()
    pc.reboot()  # hard power-off and on: no goodbye, but a new boot
    pc.tick()
    assert _wd_events() == [("watchdog_started", {"same_boot": False, "after": None})]


def test_clean_shutdown(watchdog):
    pc = Pc(watchdog)
    pc.tick()
    assert pc.goodbye("shutdown").json() == {"ok": True}
    pc.reboot()
    pc.tick()
    assert [e[0] for e in _wd_events()] == ["watchdog_shutdown", "watchdog_started"]


def test_service_stop_alerts_once(watchdog):
    pc = Pc(watchdog)
    pc.tick()
    pc.goodbye("service_stop")
    pc.awake += 30_000
    pc.start()
    pc.tick()
    assert [e[0] for e in _wd_events()] == ["tamper_service_stopped", "watchdog_started"]


def test_silent_live_watchdog_is_tamper(watchdog):
    pc = Pc(watchdog)
    pc.tick()
    pc.tick(seconds=300)  # same process, five awake minutes, never even tried: suspended
    assert _wd_events() == [("tamper_watchdog_silent", {"awake_gap_seconds": 300})]


def test_offline_with_retries_is_informational(watchdog):
    pc = Pc(watchdog)
    pc.tick()
    pc.tick(seconds=300, failed=60)
    assert _wd_events() == [("watchdog_offline", {"awake_gap_seconds": 300, "failed_attempts": 60})]


def test_sleep_does_not_count(watchdog):
    pc = Pc(watchdog)
    pc.tick()
    pc.tick(seconds=5)  # wall clock may have jumped by hours; the awake clock did not
    assert _wd_events() == []


def test_incomplete_telemetry_is_rejected(watchdog):
    r = watchdog.post("/v1/heartbeat", {"session_active": True, "user_active": True,
                                        "current_time_utc": "2026-11-04T11:00:00Z",
                                        "instance_id": "x"}, verify=False)
    assert r.status_code == 422


def test_heartbeat_without_telemetry_still_works(watchdog):
    assert watchdog.heartbeat()["lock_required"] in (True, False)
    assert _wd_events() == []


def test_bad_goodbye_reason_is_rejected(watchdog):
    r = watchdog.post("/v1/goodbye", {"instance_id": "x", "reason": "whatever"}, verify=False)
    assert r.status_code == 422


def test_lockscreen_kill_loop_is_an_alert(client, watchdog):
    r = watchdog.post("/v1/tamper_event", {"type": "lockscreen_killed", "kills": 3, "window_seconds": 60,
                                           "logged_off": True})
    assert r.json() == {"ok": True}
    assert _events("tamper_lockscreen") == [
        ("tamper_lockscreen_killed", {"kills": 3, "window_seconds": 60, "logged_off": True})]


def test_unknown_tamper_event_is_rejected(watchdog):
    assert watchdog.post("/v1/tamper_event", {"type": "anything"}, verify=False).status_code == 422


def test_offline_is_stamped_when_the_outage_began(watchdog):
    """Reported on reconnect, but the journal must place it before what happened during the outage."""
    pc = Pc(watchdog)
    pc.tick()
    pc.tick(seconds=300, failed=60)
    with get_connection() as conn:
        ts = conn.execute("SELECT ts_utc FROM events WHERE event_type = 'watchdog_offline'").fetchone()[0]
    age = datetime.now(timezone.utc) - datetime.fromisoformat(ts)
    assert timedelta(seconds=295) < age < timedelta(seconds=310)


def _guardian_report(watchdog, **over):
    body = {"type": "guardian_repaired", "check": "service_disabled", "repaired": True, "record_id": 4711,
            "occurred_utc": (datetime.now(timezone.utc) - timedelta(minutes=3)).isoformat()}
    body.update(over)
    return watchdog.post("/v1/tamper_event", body, verify=False)


def test_guardian_repair_is_an_alert_stamped_when_it_happened(watchdog):
    assert _guardian_report(watchdog).status_code == 200
    assert _events("tamper_guardian") == [
        ("tamper_guardian_repaired", {"check": "service_disabled", "repaired": True, "record_id": 4711})]
    with get_connection() as conn:
        ts = conn.execute("SELECT ts_utc FROM events WHERE event_type = 'tamper_guardian_repaired'").fetchone()[0]
    age = datetime.now(timezone.utc) - datetime.fromisoformat(ts)
    assert timedelta(minutes=2) < age < timedelta(minutes=4)


def test_guardian_report_resent_after_a_watchdog_restart_is_logged_once(watchdog):
    occurred = "2026-11-07T10:00:00+00:00"
    with time_machine.travel(datetime(2026, 11, 7, 10, 5, tzinfo=timezone.utc), tick=False):
        for _ in range(2):
            assert _guardian_report(watchdog, occurred_utc=occurred).status_code == 200
        # Same record id after the Event Log was cleared, but a different time: a new event.
        assert _guardian_report(watchdog, occurred_utc="2026-11-07T10:04:00Z").status_code == 200
    assert len(_events("tamper_guardian")) == 2


def test_malformed_guardian_reports_are_rejected(watchdog):
    for over in ({"check": "rm_rf"}, {"repaired": "yes"}, {"record_id": -1}, {"record_id": True},
                 {"occurred_utc": "yesterday"}, {"occurred_utc": "2026-11-07T10:00:00"},
                 {"occurred_utc": "2020-01-01T00:00:00Z"}):
        assert _guardian_report(watchdog, **over).status_code == 422, over
    assert _events("tamper_guardian") == []


def _stamps(event_type: str) -> list[datetime]:
    with get_connection() as conn:
        rows = conn.execute("SELECT ts_utc FROM events WHERE event_type = ? ORDER BY id", (event_type,)).fetchall()
    return [datetime.fromisoformat(r[0]) for r in rows]


def test_sleep_or_fast_startup_is_a_shutdown_and_a_start(client, watchdog):
    """Same process, hours on the server's clock, seconds on the awake clock: the PC was off (#130)."""
    pc = Pc(watchdog)
    pc.tick()
    before = datetime.now(timezone.utc)
    with time_machine.travel(before + timedelta(hours=8), tick=True):
        pc.tick(seconds=20)
        assert _wd_events() == [
            ("watchdog_shutdown", {"instance_id": pc.instance, "sleep": True}),
            ("watchdog_started", {"same_boot": False, "after": "sleep", "asleep_seconds": 8 * 3600 - 20}),
        ]
        (off,), (on,) = _stamps("watchdog_shutdown"), _stamps("watchdog_started")
        assert abs((off - before).total_seconds()) < 5
        assert abs((on - (before + timedelta(hours=8) - timedelta(seconds=20))).total_seconds()) < 5
        # Both reach the journal (PC on/off), the start newest.
        with get_connection() as conn:
            set_config_value(conn, "parent_password_hash", hash_password("haslo"))
        client.post("/admin/login", data={"password": "haslo"})
        types = [e["type"] for d in client.get("/admin/api/journal").json()["days"] for e in d["entries"]]
        assert [t for t in types if t.startswith("watchdog_")] == ["watchdog_started", "watchdog_shutdown"]


def test_short_sleep_and_a_slow_server_leave_no_entry(watchdog):
    pc = Pc(watchdog)
    pc.tick()
    with time_machine.travel(datetime.now(timezone.utc) + timedelta(seconds=100), tick=True):
        pc.tick(seconds=5)  # 95 s asleep: under the threshold
    with time_machine.travel(datetime.now(timezone.utc) + timedelta(minutes=10), tick=True):
        pc.tick(seconds=600)  # awake the whole time, the server was down: no sleep (the outage itself is logged)
    assert [e[0] for e in _wd_events()] == ["tamper_watchdog_silent"]
