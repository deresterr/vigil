"""Tamper detection from watchdog telemetry (app/tamper.py, issue #7)."""

import json

from app.db import get_connection
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
                                        "current_time_utc": "2026-11-04T11:00:00Z", "uptime_seconds": 1,
                                        "instance_id": "x"}, verify=False)
    assert r.status_code == 422


def test_heartbeat_without_telemetry_still_works(watchdog):
    assert watchdog.heartbeat()["lock_required"] in (True, False)
    assert _wd_events() == []


def test_bad_goodbye_reason_is_rejected(watchdog):
    r = watchdog.post("/v1/goodbye", {"instance_id": "x", "reason": "whatever"}, verify=False)
    assert r.status_code == 422
