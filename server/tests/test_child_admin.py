"""The child's account is an administrator (#165): reported in the heartbeat, shown in Settings, an alert when it
changes from no to yes."""

import json

import app.db
from app.crypto import hash_password


def login(client) -> None:
    with app.db.get_connection() as conn:
        app.db.set_config_value(conn, "parent_password_hash", hash_password("haslo-rodzica"))
    assert client.post("/admin/login", data={"password": "haslo-rodzica"}).status_code == 200


def device(client) -> dict:
    return client.get("/admin/api/status").json()["child"]["devices"][0]


def alerts() -> list[dict]:
    with app.db.get_connection() as conn:
        rows = conn.execute("SELECT child_id, device_id, details_json FROM events "
                            "WHERE event_type = 'tamper_child_admin' ORDER BY id").fetchall()
    return [{"child_id": r[0], "device_id": r[1], **json.loads(r[2] or "{}")} for r in rows]


def test_reported_and_shown(client, watchdog):
    login(client)
    watchdog.heartbeat()
    assert device(client)["child_admin"] is None  # a client up to 0.9 says nothing
    watchdog.heartbeat(child_admin=False)
    assert device(client)["child_admin"] is False
    watchdog.heartbeat(child_admin=True)
    assert device(client)["child_admin"] is True


def test_alert_only_when_no_becomes_yes(client, watchdog):
    watchdog.heartbeat(child_admin=True)    # first report "yes": the one-account setup, chosen in the installer
    watchdog.heartbeat(child_admin=True)
    assert alerts() == []
    watchdog.heartbeat(child_admin=False)   # the parent took the rights away
    watchdog.heartbeat(child_admin=True)    # ... and the child has them again
    watchdog.heartbeat(child_admin=True)
    assert alerts() == [{"child_id": 1, "device_id": 1}]
    watchdog.heartbeat()                    # not reported (an older client put back): no alert either way
    watchdog.heartbeat(child_admin=True)
    assert len(alerts()) == 1


def test_rights_taken_away_is_a_journal_entry(client, watchdog):
    login(client)
    watchdog.heartbeat(child_admin=True)    # the one-account setup
    watchdog.heartbeat(child_admin=False)   # the parent made the account a standard one
    watchdog.heartbeat(child_admin=False)
    watchdog.heartbeat()                    # not reported: nothing
    watchdog.heartbeat(child_admin=False)
    entries = [e for e in client.get("/admin/api/journal?days=1").json()["days"][0]["entries"]
               if e["type"] == "child_admin_removed"]
    assert len(entries) == 1
    assert entries[0]["alert"] is False


def test_the_alert_is_an_alert(client, watchdog):
    login(client)
    watchdog.heartbeat(child_admin=False)
    watchdog.heartbeat(child_admin=True)
    entry = next(e for e in client.get("/admin/api/journal?days=1").json()["days"][0]["entries"]
                 if e["type"] == "tamper_child_admin")
    assert entry["alert"] is True
