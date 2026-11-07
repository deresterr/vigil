"""Offline TOTP unlock support on the server (#9)."""

from datetime import datetime
from zoneinfo import ZoneInfo

import pyotp
import time_machine

from app.crypto import hash_password
from app.db import get_connection, set_config_value
from app.offline import totp_fingerprint
from tests.conftest import config

TZ = ZoneInfo("Europe/Warsaw")
NOON = datetime(2026, 11, 4, 12, 0, tzinfo=TZ)


def test_secret_is_sent_only_when_fingerprint_differs(watchdog):
    r = watchdog.heartbeat(totp_fp="")
    assert r["totp_secret"] == config("totp_secret")
    r = watchdog.heartbeat(totp_fp=totp_fingerprint(config("totp_secret")))
    assert "totp_secret" not in r


def test_watchdog_without_fingerprint_never_gets_the_secret(watchdog):
    assert "totp_secret" not in watchdog.heartbeat()


def test_regenerated_secret_reaches_the_watchdog(client, watchdog):
    old_fp = totp_fingerprint(config("totp_secret"))
    with get_connection() as conn:
        set_config_value(conn, "totp_secret", pyotp.random_base32())
    assert watchdog.heartbeat(totp_fp=old_fp)["totp_secret"] == config("totp_secret")


def test_offline_grant_is_logged_once_and_burns_the_step(client, watchdog):
    with time_machine.travel(NOON, tick=False):
        with get_connection() as conn:
            set_config_value(conn, "parent_password_hash", hash_password("x"))
        client.post("/admin/login", data={"password": "x"})
        totp = pyotp.TOTP(config("totp_secret"))
        step = totp.timecode(datetime.now(TZ))
        grant = {"at_utc": "2026-11-04T10:30:00Z", "minutes": 60, "step": step}
        watchdog.heartbeat(totp_fp="", offline_grants=[grant])
        watchdog.heartbeat(totp_fp="", offline_grants=[grant])  # resent after a lost response
        entries = client.get("/admin/api/journal").json()["days"][0]["entries"]
        offline = [e for e in entries if e["type"] == "offline_unlock"]
        assert len(offline) == 1 and offline[0]["time"] == "11:30" and offline[0]["details"]["minutes"] == 60
        # the code used offline does not work online any more
        r = watchdog.post("/v1/totp_attempt", {"code": totp.now()})
        assert r.status_code == 401


def test_bad_offline_grants_are_rejected(watchdog):
    r = watchdog.post("/v1/heartbeat", {"session_active": True, "user_active": True,
                                        "current_time_utc": "2026-11-04T11:00:00Z", "uptime_seconds": 1,
                                        "offline_grants": [{"minutes": "60"}]}, verify=False)
    assert r.status_code == 422
