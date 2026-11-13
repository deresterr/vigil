"""D7 (#40): several children, each with several PCs. Time and locks are per child."""

from datetime import datetime
from zoneinfo import ZoneInfo

import pyotp
import time_machine

from app.crypto import hash_password
from app.db import get_connection, set_config_value
from tests.conftest import Watchdog, add_child, add_device, config, set_schedule, set_used, state_for

TZ = ZoneInfo("Europe/Warsaw")
NOON = datetime(2026, 11, 4, 12, 0, tzinfo=TZ)  # Wednesday
DAY = "2026-11-04"
PASSWORD = "haslo-rodzica"


def _login(client):
    with get_connection() as conn:
        set_config_value(conn, "parent_password_hash", hash_password(PASSWORD))
    assert client.post("/admin/login", data={"password": PASSWORD}).status_code == 200


def _family(client):
    """Ola (child 1) with PC (device 1) and Laptop (2); Kuba (child 2) with his PC (3)."""
    add_device(1, "Laptop")
    add_child("Kuba")
    add_device(2, "PC Kuby")
    return Watchdog(client, 1), Watchdog(client, 2), Watchdog(client, 3)


def _run(t, *watchdogs, seconds=60):
    for w in watchdogs:
        w.heartbeat()
    for _ in range(seconds // 5):
        t.shift(5)
        for w in watchdogs:
            w.heartbeat()


def test_two_pcs_of_one_child_add_up(client):
    pc, laptop, kuba = _family(client)
    with time_machine.travel(NOON, tick=False) as t:
        _run(t, pc, laptop)
    assert state_for(DAY, 1)["seconds_used"] == 120  # decision: time = sum over devices
    assert state_for(DAY, 2) == {}
    with get_connection() as conn:
        assert [r[0] for r in conn.execute("SELECT seconds_today FROM devices ORDER BY id")] == [60, 60, 0]


def test_limit_of_one_child_locks_all_its_pcs_and_nobody_else(client):
    pc, laptop, kuba = _family(client)
    set_schedule(1, daily_limit=1)
    with time_machine.travel(NOON, tick=False) as t:
        _run(t, pc, laptop, kuba, seconds=30)  # Ola: 60 s over two PCs = her 1 min
        assert pc.heartbeat()["lock_reason"] == "limit"
        assert laptop.heartbeat()["lock_reason"] == "limit"
        assert kuba.heartbeat()["lock_required"] is False
    with get_connection() as conn:
        rows = conn.execute("SELECT child_id FROM events WHERE event_type = 'lock_started'").fetchall()
    assert [r[0] for r in rows] == [1]  # once per child, not once per PC


def test_pause_on_one_pc_pauses_the_child(client):
    pc, laptop, kuba = _family(client)
    with time_machine.travel(NOON, tick=False):
        assert pc.post("/v1/pause_toggle", {}).json() == {"ok": True, "paused": True}
        assert laptop.heartbeat()["lock_reason"] == "pause"
        assert kuba.heartbeat()["lock_required"] is False


def test_resume_with_a_target_is_idempotent(client):
    """"I'm back" sends paused=false: a double click (or one per monitor) must not start the pause again (#86)."""
    pc, laptop, kuba = _family(client)
    with time_machine.travel(NOON, tick=False):
        assert pc.post("/v1/pause_toggle", {}).json() == {"ok": True, "paused": True}
        for _ in range(2):
            assert pc.post("/v1/pause_toggle", {"paused": False}).json() == {"ok": True, "paused": False}
        assert laptop.heartbeat()["lock_required"] is False
        with get_connection() as conn:
            assert conn.execute("SELECT COUNT(*) FROM events WHERE event_type = 'pause_toggled'").fetchone()[0] == 2


def test_totp_grant_goes_to_the_child_of_the_pc(client):
    pc, laptop, kuba = _family(client)
    set_schedule(1, daily_limit=1)
    set_schedule(2, daily_limit=1)
    with time_machine.travel(NOON, tick=False):
        laptop.heartbeat()
        kuba.heartbeat()
        set_used(DAY, 1, child_id=1)
        set_used(DAY, 1, child_id=2)
        code = pyotp.TOTP(config("totp_secret")).now()
        r = laptop.post("/v1/totp_attempt", {"code": code, "minutes_to_grant": 30}).json()
        assert r["mode"] == "extend_limit"
        assert pc.heartbeat()["lock_required"] is False
        assert kuba.heartbeat()["lock_reason"] == "limit"
    assert state_for(DAY, 1)["limit_override_minutes"] == 30
    assert state_for(DAY, 2)["limit_override_minutes"] == 0


def test_panel_status_lists_children_and_the_selected_ones_pcs(client):
    pc, laptop, kuba = _family(client)
    with time_machine.travel(NOON, tick=False) as t:
        _login(client)  # inside: a session cookie from the future would be rejected
        _run(t, pc, kuba)
        assert client.post("/admin/api/children/2/manual_lock", json={"locked": True}).json()["ok"]
        s = client.get("/admin/api/status?child_id=1").json()
        kuba_status = client.get("/admin/api/status?child_id=2").json()["child"]
        assert client.get("/admin/api/status?child_id=9").status_code == 404
    assert [(c["id"], c["name"], c["lock_reason"], c["devices_online"], c["devices_total"])
            for c in s["children"]] == [(1, "Ola", None, 1, 2), (2, "Kuba", "manual", 1, 1)]
    assert s["devices_total"] == 3
    assert [(d["name"], d["online"], d["minutes_today"]) for d in s["child"]["devices"]] == [
        ("PC", True, 1), ("Laptop", False, 0)]
    assert kuba_status["lock_reason"] == "manual"


def test_child_settings_and_validation(client):
    _login(client)
    r = client.post("/admin/api/children", json={"name": "Kuba"})
    assert r.json() == {"ok": True, "id": 2}
    week = client.get("/admin/api/status?child_id=2").json()["child"]["schedule"]
    assert [d["daily_limit"] for d in week] == [120] * 4 + [240] * 3  # defaults: Friday as weekend
    week[1] = {"daily_limit": 45, "night_start": "20:00", "night_end": "07:00"}
    assert client.patch("/admin/api/children/2", json={"schedule": week, "name": " Jakub "}).json()[
        "updated"] == ["name", "schedule"]
    child = client.get("/admin/api/status?child_id=2").json()["child"]
    assert (child["name"], child["schedule"][1], child["schedule"][0]["daily_limit"]) == (
        "Jakub", {"daily_limit": 45, "night_start": "20:00", "night_end": "07:00"}, 120)
    day = {"daily_limit": 60, "night_start": "21:00", "night_end": "07:00"}
    for bad in ({"name": ""}, {"schedule": [day] * 6}, {"schedule": [day] * 8},
                {"schedule": [{**day, "daily_limit": 2000}] * 7}, {"schedule": [{**day, "daily_limit": "60"}] * 7},
                {"schedule": [{**day, "night_start": "25:00"}] * 7},
                {"schedule": [{**day, "night_end": "21:00"}] * 7},  # empty night
                {"schedule": [{**day, "extra": 1}] * 7}, {"unknown": 1}):
        assert client.patch("/admin/api/children/2", json=bad).status_code == 422, bad
    assert client.patch("/admin/api/children/9", json={"name": "x"}).status_code == 404


def test_new_child_with_its_own_week(client):
    _login(client)
    week = [{"daily_limit": 10 * (i + 1), "night_start": "21:00", "night_end": "07:00"} for i in range(7)]
    assert client.post("/admin/api/children", json={"name": "Kuba", "schedule": week}).json()["id"] == 2
    assert client.get("/admin/api/status?child_id=2").json()["child"]["schedule"] == week
    assert client.delete("/admin/api/children/2").json()["ok"] is True
    with get_connection() as conn:
        assert conn.execute("SELECT COUNT(*) FROM child_schedule WHERE child_id = 2").fetchone()[0] == 0


def test_journal_names_child_and_device_and_filters(client):
    pc, laptop, kuba = _family(client)
    with time_machine.travel(NOON, tick=False):
        _login(client)
        laptop.post("/v1/pause_toggle", {})
        kuba.post("/v1/pause_toggle", {})
        client.post("/admin/api/children/1/grant", json={"minutes": 15, "mode": "limit"})
        everyone = client.get("/admin/api/journal?days=1").json()["days"][0]
        ola = client.get("/admin/api/journal?days=1&child_id=1").json()["days"][0]
    assert [(e["type"], e["child_name"], e["device_name"]) for e in everyone["entries"]] == [
        ("grant_applied", "Ola", None), ("pause", "Kuba", "PC Kuby"), ("pause", "Ola", "Laptop")]
    assert [e["child_name"] for e in ola["entries"]] == ["Ola", "Ola"]
    assert [(c["name"], c["limit_minutes"]) for c in everyone["children"]] == [("Ola", 135), ("Kuba", 120)]
    assert [c["name"] for c in ola["children"]] == ["Ola"]


def test_removed_child_and_device_keep_their_names_in_the_journal(client):
    with time_machine.travel(NOON, tick=False):
        _login(client)
        add_child("Kuba")
        client.delete("/admin/api/devices/1")
        client.delete("/admin/api/children/2")
        entries = client.get("/admin/api/journal?days=1").json()["days"][0]["entries"]
    assert [(e["type"], e["child_name"], e["device_name"]) for e in entries] == [
        ("child_removed", "Kuba", None), ("device_unpaired", "Ola", "PC")]
