from datetime import datetime, timedelta
from zoneinfo import ZoneInfo

import pyotp
import pytest
import time_machine

from app.db import get_connection
from tests.conftest import Watchdog, config, state_for

TZ = ZoneInfo("Europe/Warsaw")
NOON = datetime(2026, 11, 4, 12, 0, tzinfo=TZ)  # Wednesday, outside night window


def test_time_accumulates_while_active(watchdog):
    with time_machine.travel(NOON, tick=False) as t:
        watchdog.heartbeat()
        for _ in range(12):
            t.shift(5)
            watchdog.heartbeat()
    assert state_for("2026-11-04")["seconds_used"] == 60


def test_inactive_session_does_not_count(watchdog):
    with time_machine.travel(NOON, tick=False) as t:
        watchdog.heartbeat()
        for _ in range(12):
            t.shift(5)
            watchdog.heartbeat(active=False)
    assert state_for("2026-11-04")["seconds_used"] == 0


def test_gap_after_sleep_does_not_count(watchdog):
    with time_machine.travel(NOON, tick=False) as t:
        watchdog.heartbeat()
        t.shift(3600)  # PC asleep for an hour
        watchdog.heartbeat()
    assert state_for("2026-11-04")["seconds_used"] == 0


def test_limit_locks_and_freezes_time(watchdog):
    with time_machine.travel(NOON, tick=False) as t:
        watchdog.heartbeat()
        with get_connection() as conn:
            conn.execute("UPDATE daily_state SET seconds_used = ? WHERE date_local = ?", (120 * 60 - 5, "2026-11-04"))
        t.shift(5)
        r = watchdog.heartbeat()
        assert r["lock_required"] and r["lock_reason"] == "limit"
        for _ in range(5):
            t.shift(5)
            watchdog.heartbeat()
    assert state_for("2026-11-04")["seconds_used"] == 120 * 60


def test_totp_grant_extends_limit(watchdog):
    with time_machine.travel(NOON, tick=False):
        watchdog.heartbeat()
        with get_connection() as conn:
            conn.execute("UPDATE daily_state SET seconds_used = ? WHERE date_local = ?", (120 * 60, "2026-11-04"))
        code = pyotp.TOTP(config("totp_secret")).now()
        r = watchdog.post("/v1/totp_attempt", {"code": code, "minutes_to_grant": 30})
        assert r.json()["mode"] == "extend_limit"
        assert watchdog.heartbeat()["lock_required"] is False


@pytest.mark.xfail(strict=True, reason="BUG: night extension lives on the day row; lost at local midnight")
def test_night_extension_survives_midnight(watchdog):
    with time_machine.travel(datetime(2026, 11, 4, 23, 0, tzinfo=TZ), tick=False) as t:
        assert watchdog.heartbeat()["lock_reason"] == "night"
        code = pyotp.TOTP(config("totp_secret")).now()
        r = watchdog.post("/v1/totp_attempt", {"code": code, "minutes_to_grant": 120})  # until 01:00
        assert r.json()["mode"] == "extend_night"
        assert watchdog.heartbeat()["lock_required"] is False
        t.shift(timedelta(minutes=90))  # 00:30, still inside the granted window
        assert watchdog.heartbeat()["lock_required"] is False


@pytest.mark.xfail(strict=True, reason="SECURITY: requests are not signed and the bearer token is readable "
                                       "by the child (vigil.toml ACL) -> extra heartbeats stall the counter")
def test_injected_heartbeats_cannot_stall_counter(client):
    real, attacker = Watchdog(client), Watchdog(client)
    with time_machine.travel(NOON, tick=False) as t:
        real.heartbeat()
        for _ in range(12):  # one minute of play
            for _ in range(10):
                t.shift(0.5)
                attacker.heartbeat(active=False)
            real.heartbeat()
    assert state_for("2026-11-04")["seconds_used"] >= 55
