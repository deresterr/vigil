"""Daily limit 0 = no limit that day (#106): the PC, the panel and the journal."""

import time_machine

from tests.conftest import set_schedule, set_used, state_for
from tests.test_children import _login
from tests.test_heartbeat import NOON  # Wednesday 2026-11-04, outside the night


def test_pc_gets_no_lock_and_no_countdown(watchdog):
    set_schedule(weekdays=[2], daily_limit=0)
    with time_machine.travel(NOON, tick=False):
        watchdog.heartbeat()
        set_used("2026-11-04", 600)
        r = watchdog.heartbeat()
    assert r["lock_required"] is False
    assert r["minutes_remaining"] == 0  # vigil_ui shows no "time left" at 0


def test_panel_status_says_unlimited(client):
    set_schedule(weekdays=[2], daily_limit=0)
    with time_machine.travel(NOON, tick=False):
        _login(client)  # inside the travel: a session from the real "now" would be from the future
        child = client.get("/admin/api/status").json()["child"]
    assert (child["unlimited"], child["minutes_remaining"], child["lock_reason"]) == (True, 0, None)
    set_schedule(weekdays=[2], daily_limit=90)
    with time_machine.travel(NOON, tick=False):
        assert client.get("/admin/api/status").json()["child"]["unlimited"] is False


def test_extra_time_is_refused_but_night_time_is_not(client):
    """A bonus would turn "no limit" into a limit of that many minutes."""
    set_schedule(weekdays=[2], daily_limit=0)
    with time_machine.travel(NOON, tick=False):
        _login(client)
        r = client.post("/admin/api/children/1/grant", json={"minutes": 30, "mode": "limit"})
        assert (r.status_code, r.json()["detail"]) == (409, "no_limit")
        assert client.post("/admin/api/children/1/grant", json={"minutes": 60, "mode": "night"}).status_code == 200
    assert state_for("2026-11-04")["limit_override_minutes"] == 0


def test_journal_day_has_no_limit(client, watchdog):
    set_schedule(weekdays=[2], daily_limit=0)
    with time_machine.travel(NOON, tick=False):
        watchdog.heartbeat()
        _login(client)
        day = client.get("/admin/api/journal?days=1").json()["days"][0]
    assert day["children"][0]["limit_minutes"] is None
