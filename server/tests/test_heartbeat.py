from datetime import datetime, timedelta
from zoneinfo import ZoneInfo

import pyotp
import time_machine

from app.crypto import hash_password
from app.db import get_connection, set_config_value
from tests.conftest import Watchdog, config, set_schedule, set_used, state_for

TZ = ZoneInfo("Europe/Warsaw")
NOON = datetime(2026, 11, 4, 12, 0, tzinfo=TZ)  # Wednesday, outside night window
HB_IDLE = {"session_active": False, "user_active": False, "current_time_utc": "2026-11-04T11:00:00Z"}


def test_time_accumulates_while_active(watchdog):
    with time_machine.travel(NOON, tick=False) as t:
        watchdog.heartbeat()
        for _ in range(12):
            t.shift(5)
            watchdog.heartbeat()
    assert state_for("2026-11-04")["seconds_used"] == 60


def test_fractions_of_a_second_add_up(watchdog):
    """Heartbeats come every 5 s plus the round trip: the remainder carries over instead of being dropped (#86)."""
    with time_machine.travel(NOON, tick=False) as t:
        watchdog.heartbeat()
        for _ in range(10):
            t.shift(5.3)
            watchdog.heartbeat()
    assert state_for("2026-11-04")["seconds_used"] == 53


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
            conn.execute("UPDATE child_daily SET seconds_used = ? WHERE child_id = 1 AND date_local = ?",
                         (120 * 60 - 5, "2026-11-04"))
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
            conn.execute("UPDATE child_daily SET seconds_used = ? WHERE child_id = 1 AND date_local = ?",
                         (120 * 60, "2026-11-04"))
        code = pyotp.TOTP(config("totp_secret")).now()
        r = watchdog.post("/v1/totp_attempt", {"code": code, "minutes_to_grant": 30})
        assert r.json()["mode"] == "extend_limit"
        assert watchdog.heartbeat()["lock_required"] is False


def test_night_extension_survives_midnight(watchdog):
    with time_machine.travel(datetime(2026, 11, 4, 23, 0, tzinfo=TZ), tick=False) as t:
        assert watchdog.heartbeat()["lock_reason"] == "night"
        code = pyotp.TOTP(config("totp_secret")).now()
        r = watchdog.post("/v1/totp_attempt", {"code": code, "minutes_to_grant": 120})  # until 01:00
        assert r.json()["mode"] == "extend_night"
        assert watchdog.heartbeat()["lock_required"] is False
        t.shift(timedelta(minutes=90))  # 00:30, still inside the granted window
        assert watchdog.heartbeat()["lock_required"] is False


def test_injected_heartbeats_cannot_stall_counter(client):
    # Attacker without the signing key (e.g. another LAN device or a non-elevated process).
    real, attacker = Watchdog(client), Watchdog(client, key=b"x" * 32)
    with time_machine.travel(NOON, tick=False) as t:
        real.heartbeat()
        for _ in range(12):  # one minute of play
            for _ in range(10):
                t.shift(0.5)
                r = attacker.post("/v1/heartbeat", HB_IDLE, verify=False)
                assert r.status_code == 401
            real.heartbeat()
    assert state_for("2026-11-04")["seconds_used"] >= 55


def _grant_night(watchdog, minutes: int) -> None:
    code = pyotp.TOTP(config("totp_secret")).now()
    r = watchdog.post("/v1/totp_attempt", {"code": code, "minutes_to_grant": minutes})
    assert r.json()["mode"] == "extend_night"


def test_expired_night_extension_is_not_carried_over(watchdog):
    with time_machine.travel(datetime(2026, 11, 4, 23, 0, tzinfo=TZ), tick=False) as t:
        watchdog.heartbeat()
        _grant_night(watchdog, 30)  # until 23:30
        t.shift(timedelta(hours=2))  # 01:00, first request of the new day
        assert watchdog.heartbeat()["lock_reason"] == "night"
    assert state_for("2026-11-05")["night_extension_until_utc"] is None


def test_admin_sees_and_clears_extension_after_midnight(client, watchdog):
    with get_connection() as conn:
        set_config_value(conn, "parent_password_hash", hash_password("haslo-rodzica"))
    with time_machine.travel(datetime(2026, 11, 4, 23, 0, tzinfo=TZ), tick=False) as t:
        watchdog.heartbeat()
        _grant_night(watchdog, 120)  # until 01:00
        t.shift(timedelta(minutes=90))  # 00:30, new local day
        assert client.post("/admin/login", data={"password": "haslo-rodzica"}).status_code == 200
        status = client.get("/admin/api/status").json()["child"]
        assert status["night_extension_until_local"] == "2026-11-05 01:00"
        assert status["lock_required"] is False
        assert client.post("/admin/api/children/1/clear_extension").json()["ok"] is True
        assert watchdog.heartbeat()["lock_reason"] == "night"


def _wrong(code: str) -> str:
    return code[:-1] + str((int(code[-1]) + 1) % 10)


def test_totp_code_cannot_be_replayed(watchdog):
    with time_machine.travel(NOON, tick=False):
        watchdog.heartbeat()
        with get_connection() as conn:
            conn.execute("UPDATE child_daily SET seconds_used = ? WHERE child_id = 1 AND date_local = ?",
                         (120 * 60, "2026-11-04"))
        code = pyotp.TOTP(config("totp_secret")).now()
        assert watchdog.post("/v1/totp_attempt", {"code": code, "minutes_to_grant": 30}).status_code == 200
        r = watchdog.post("/v1/totp_attempt", {"code": code, "minutes_to_grant": 30})
        assert r.status_code == 401 and r.json()["reason"] == "invalid_code"
    assert state_for("2026-11-04")["limit_override_minutes"] == 30


def test_totp_lockout_is_capped(watchdog):
    seen = []
    with time_machine.travel(NOON, tick=False) as t:
        for _ in range(12):
            bad = _wrong(pyotp.TOTP(config("totp_secret")).now())
            r = watchdog.post("/v1/totp_attempt", {"code": bad})
            if r.json().get("lockout_seconds_remaining"):
                seen.append(r.json()["lockout_seconds_remaining"])
                t.shift(r.json()["lockout_seconds_remaining"])
    assert seen == [60, 300, 900, 900]


def test_totp_lockout_blocks_valid_code_until_it_expires(watchdog):
    with time_machine.travel(NOON, tick=False) as t:
        watchdog.heartbeat()
        for _ in range(3):
            watchdog.post("/v1/totp_attempt", {"code": _wrong(pyotp.TOTP(config("totp_secret")).now())})
        r = watchdog.post("/v1/totp_attempt", {"code": pyotp.TOTP(config("totp_secret")).now()})
        assert r.status_code == 429
        t.shift(61)
        r = watchdog.post("/v1/totp_attempt", {"code": pyotp.TOTP(config("totp_secret")).now()})
        assert r.status_code == 200


def test_settings_change_applies_on_the_next_heartbeat(watchdog):
    """No config cache: a limit changed in the panel counts at once, not after a TTL."""
    with time_machine.travel(NOON, tick=False):
        assert watchdog.heartbeat()["lock_required"] is False
        set_used("2026-11-04", 30)
        set_schedule(daily_limit=30)
        assert watchdog.heartbeat()["lock_reason"] == "limit"


def test_server_sends_codes_not_sentences(watchdog):
    """i18n (#52): vigil_ui words the lock screen in the child's Windows language; the server sends codes."""
    with time_machine.travel(datetime(2026, 11, 4, 23, 0, tzinfo=TZ), tick=False):
        hb = watchdog.heartbeat()
        assert hb["lock_reason"] == "night" and "main_message" not in hb["ui_state"]
        code = pyotp.TOTP(config("totp_secret")).now()
        r = watchdog.post("/v1/totp_attempt", {"code": code, "minutes_to_grant": 30})
        assert r.json() == {"ok": True, "mode": "extend_night", "minutes": 30, "until_local": "2026-11-04 23:30"}


def test_night_lock_tells_when_it_ends(watchdog):
    """The lock screen says "the computer unlocks at 06:30": the end of the night in progress, as a time only."""
    with time_machine.travel(datetime(2026, 11, 4, 23, 0, tzinfo=TZ), tick=False) as t:
        assert watchdog.heartbeat()["ui_state"]["night_end_local"] == "06:30"
        t.shift(timedelta(hours=3))  # 02:00, after midnight: still the same night
        assert watchdog.heartbeat()["ui_state"]["night_end_local"] == "06:30"
    with time_machine.travel(NOON, tick=False):  # a limit lock: no night end
        set_used("2026-11-04", 120)
        hb = watchdog.heartbeat()
        assert hb["lock_reason"] == "limit" and "night_end_local" not in hb["ui_state"]
