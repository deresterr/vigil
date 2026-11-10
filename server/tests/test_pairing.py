from datetime import datetime, timedelta

import time_machine

from app.crypto import hash_password
from app.db import get_connection, set_config_value
from tests.conftest import Watchdog, add_child

PASSWORD = "haslo-rodzica"
HB = {"session_active": True, "user_active": True, "current_time_utc": "2026-11-04T11:00:00Z", "uptime_seconds": 1}


def _login(client):
    with get_connection() as conn:
        set_config_value(conn, "parent_password_hash", hash_password(PASSWORD))
    client.post("/admin/login", data={"password": PASSWORD})


def _new_code(client, **target) -> str:
    r = client.post("/admin/api/pairing_code", json=target or {"child_id": 1, "device_name": "Laptop"})
    assert r.status_code == 200, r.text
    return r.json()["code"]


def _devices() -> list[tuple]:
    with get_connection() as conn:
        return [tuple(r) for r in conn.execute("SELECT id, child_id, name FROM devices ORDER BY id")]


def test_pair_without_code_is_rejected(client):
    r = client.post("/v1/pair", json={"code": "12345678"})
    assert r.status_code == 404


def test_pairing_adds_a_device_of_the_child_and_is_single_use(client):
    _login(client)
    code = _new_code(client)
    assert client.get("/admin/api/status").json()["pairing_active"] is True

    r = client.post("/v1/pair", json={"code": code})  # "1234 5678" format is accepted
    assert r.status_code == 200
    assert set(r.json()) == {"signing_key"}  # the address is the one the installer reached
    assert _devices() == [(1, 1, "PC"), (2, 1, "Laptop")]

    assert client.post("/v1/pair", json={"code": code}).status_code == 404
    assert client.get("/admin/api/status").json()["pairing_active"] is False
    # Both PCs of the child authenticate, each with its own key.
    assert Watchdog(client, 1).post("/v1/heartbeat", HB).status_code == 200
    assert Watchdog(client, 2).post("/v1/heartbeat", HB).status_code == 200


def test_repairing_a_device_rotates_only_its_key(client):
    _login(client)
    old = Watchdog(client, 1)
    code = _new_code(client, device_id=1).replace(" ", "")
    assert client.post("/v1/pair", json={"code": code}).status_code == 200
    assert _devices() == [(1, 1, "PC")]
    assert old.post("/v1/heartbeat", HB, verify=False).status_code == 401
    assert Watchdog(client, 1).post("/v1/heartbeat", HB).status_code == 200


def test_pairing_code_needs_exactly_one_target(client):
    _login(client)
    for body in ({}, {"child_id": 1, "device_id": 1}, {"device_name": "x"}):
        assert client.post("/admin/api/pairing_code", json=body).status_code == 422, body
    assert client.post("/admin/api/pairing_code", json={"child_id": 99, "device_name": "x"}).status_code == 404
    assert client.post("/admin/api/pairing_code", json={"device_id": 99}).status_code == 404


def test_unpaired_device_is_rejected_and_its_child_keeps_the_others(client):
    _login(client)
    add_child("Kuba")
    client.post("/v1/pair", json={"code": _new_code(client)})
    gone = Watchdog(client, 1)
    assert client.delete("/admin/api/devices/1").json() == {"ok": True}
    assert gone.post("/v1/heartbeat", HB, verify=False).status_code == 401
    assert Watchdog(client, 2).post("/v1/heartbeat", HB).status_code == 200
    # A child with devices cannot be removed; one without can.
    assert client.delete("/admin/api/children/1").status_code == 409
    assert client.delete("/admin/api/children/2").json() == {"ok": True}


def test_too_many_wrong_codes_invalidate_the_code(client):
    _login(client)
    code = _new_code(client).replace(" ", "")
    wrong = code[:-1] + str((int(code[-1]) + 1) % 10)
    lefts = [client.post("/v1/pair", json={"code": wrong}).json()["attempts_left"] for _ in range(5)]
    assert lefts == [4, 3, 2, 1, 0]
    assert client.post("/v1/pair", json={"code": code}).status_code == 404


def test_code_expires(client):
    _login(client)
    with time_machine.travel(datetime.now().astimezone(), tick=False) as t:
        code = _new_code(client)
        t.shift(timedelta(minutes=11))
        assert client.post("/v1/pair", json={"code": code}).status_code == 404


def test_new_code_replaces_previous(client):
    _login(client)
    first = _new_code(client)
    second = _new_code(client)
    if first != second:
        assert client.post("/v1/pair", json={"code": first}).status_code == 401
    assert client.post("/v1/pair", json={"code": second}).status_code == 200


def test_code_must_be_a_string(client):
    """A number would lose leading zeros ("01234567"): only the string form is accepted."""
    _login(client)
    code = _new_code(client).replace(" ", "")
    assert client.post("/v1/pair", json={"code": int(code)}).status_code == 422
    assert client.post("/v1/pair", json={"code": code}).status_code == 200
