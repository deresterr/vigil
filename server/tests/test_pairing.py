from datetime import datetime, timedelta

import time_machine

from app.crypto import hash_password
from app.db import get_connection, set_config_value
from tests.conftest import Watchdog, config

PASSWORD = "haslo-rodzica"
HB = {"session_active": True, "user_active": True, "current_time_utc": "2026-11-04T11:00:00Z", "uptime_seconds": 1}


def _login(client):
    with get_connection() as conn:
        set_config_value(conn, "parent_password_hash", hash_password(PASSWORD))
    client.post("/admin/login", data={"password": PASSWORD})


def _new_code(client) -> str:
    r = client.post("/admin/api/pairing_code", json={})
    assert r.status_code == 200
    return r.json()["code"]


def test_pair_without_code_is_rejected(client):
    r = client.post("/v1/pair", json={"code": "12345678"})
    assert r.status_code == 404


def test_pairing_rotates_key_and_is_single_use(client):
    _login(client)
    old = Watchdog(client)
    code = _new_code(client)
    assert client.get("/admin/api/status").json()["pairing_active"] is True

    r = client.post("/v1/pair", json={"code": code})  # "1234 5678" format is accepted
    assert r.status_code == 200
    assert r.json()["signing_key"] == config("signing_key")
    assert r.json()["server_url"]

    assert client.post("/v1/pair", json={"code": code}).status_code == 404
    assert client.get("/admin/api/status").json()["pairing_active"] is False
    assert old.post("/v1/heartbeat", HB, verify=False).status_code == 401
    assert Watchdog(client).post("/v1/heartbeat", HB).status_code == 200


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
