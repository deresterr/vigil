"""The server's clock against NTP (#161): the SNTP exchange, the check's states, the Settings card's data,
`clock_drift`'s wording and where the parent sets the zone and the NTP server."""

import asyncio
import json
import logging
import socket
import time
from datetime import UTC, datetime, timedelta

import pytest
from fastapi.testclient import TestClient

import app.admin_auth
import app.clock
import app.db
from app.clock import _ntp_bytes, parse_reply, request_packet
from app.clock import sntp_offset as real_sntp_offset  # before conftest's autouse fake replaces it
from app.crypto import hash_password
from tests.conftest import config

PASSWORD = "haslo-rodzica"


def reply(request: bytes, t2: float, t3: float, *, first: int = 0x24, stratum: int = 2,
          origin: bytes | None = None) -> bytes:
    """A server's answer: LI 0, version 4, mode 4 (first byte 0x24)."""
    head = bytes([first, stratum]) + bytes(22)
    return head + (origin if origin is not None else request[40:48]) + _ntp_bytes(t2) + _ntp_bytes(t3)


# === The exchange ===


def test_offset_from_the_four_timestamps():
    """We send at 1000.0 by our clock, the server (5 s ahead) receives at 1005.1 and answers at 1005.2, we get it at
    1000.3: 0.1 s each way, offset 5 s."""
    req = request_packet(1000.0)
    assert parse_reply(reply(req, 1005.1, 1005.2), req, 1000.0, 1000.3) == pytest.approx(5.0, abs=1e-6)


@pytest.mark.parametrize("bad", [
    {"first": 0x23},                 # client mode, not a server's answer
    {"first": 0xE4},                 # leap 3: the server's clock is not synchronized
    {"stratum": 0},                  # kiss-o'-death
    {"stratum": 16},                 # unsynchronized
    {"origin": _ntp_bytes(1.0)},     # not an answer to our request
])
def test_untrustworthy_answers_are_ignored(bad):
    req = request_packet(1000.0)
    assert parse_reply(reply(req, 1005.1, 1005.2, **bad), req, 1000.0, 1000.3) is None
    assert parse_reply(reply(req, 1005.1, 1005.2)[:47], req, 1000.0, 1000.3) is None


def test_sntp_over_udp():
    """A real exchange with a local server 90 s ahead, and a port where nobody answers."""
    async def run():
        loop = asyncio.get_running_loop()

        class Server(asyncio.DatagramProtocol):
            def connection_made(self, transport):
                self.transport = transport

            def datagram_received(self, data, addr):
                now = time.time() + 90
                self.transport.sendto(reply(data, now, now), addr)

        server, _ = await loop.create_datagram_endpoint(Server, local_addr=("127.0.0.1", 0))
        port = server.get_extra_info("sockname")[1]
        try:
            offset = await real_sntp_offset("127.0.0.1", port, timeout=2)
        finally:
            server.close()
        with socket.socket(socket.AF_INET, socket.SOCK_DGRAM) as s:  # a free port, nobody behind it
            s.bind(("127.0.0.1", 0))
            silent = s.getsockname()[1]
        return offset, await real_sntp_offset("127.0.0.1", silent, timeout=0.3)

    offset, silent = asyncio.run(run())
    assert offset == pytest.approx(90, abs=1)
    assert silent is None


# === The check ===


def check(client) -> dict:
    return client.portal.call(app.clock.check_clock)


def events(event_type: str) -> list[dict]:
    with app.db.get_connection() as conn:
        rows = conn.execute("SELECT details_json, child_id FROM events WHERE event_type = ? ORDER BY id",
                            (event_type,)).fetchall()
    return [{"child_id": r["child_id"], **json.loads(r["details_json"] or "{}")} for r in rows]


def test_states_logs_and_one_alert_per_episode(client, ntp, caplog):
    caplog.set_level(logging.INFO, logger="vigil")
    caplog.clear()
    assert check(client)["state"] == "ok"
    assert ntp.asked[-1] == "pool.ntp.org"  # the default on a database without the key

    ntp.offset = 240.4  # NTP 4 min ahead: the server's clock is slow
    assert check(client) | {"checked_utc": None} == {"state": "off", "offset_seconds": 240, "server": "pool.ntp.org",
                                                     "checked_utc": None}
    check(client)  # still off: no second alert, no second log line
    assert events("server_clock_off") == [{"child_id": None, "offset_seconds": 240, "ntp_server": "pool.ntp.org"}]

    ntp.offset = 0.4
    assert check(client)["state"] == "ok"
    ntp.offset = None
    assert check(client)["state"] == "no_answer"
    check(client)
    ntp.offset = 1.0
    assert check(client)["state"] == "ok"
    assert [r.getMessage() for r in caplog.records] == [
        "Server clock is 240 s slow against NTP (pool.ntp.org): set up time sync on the host",
        "Server clock in sync with NTP again",
        "NTP server pool.ntp.org does not answer: the server's clock is not checked",
        "NTP server pool.ntp.org answers again",
    ]
    assert len(events("server_clock_off")) == 1  # no answer is not an alert


def test_within_a_minute_is_in_sync(client, ntp):
    ntp.offset = -60
    assert check(client)["state"] == "ok"
    ntp.offset = -61
    assert check(client)["state"] == "off"


def test_no_server_turns_the_check_off(client, ntp):
    with app.db.get_connection() as conn:
        app.db.set_config_value(conn, "ntp_server", "")
    asked = len(ntp.asked)
    assert check(client)["state"] == "disabled"
    assert len(ntp.asked) == asked


def test_clock_drift_says_whose_clock_is_wrong_only_with_ntp(client, watchdog, ntp):
    """The PC's clock 10 min fast: blamed on the PC only when NTP vouches for the server's clock."""
    def drift_event() -> dict:
        watchdog.post("/v1/heartbeat", {"session_active": True, "user_active": True,
                                        "current_time_utc": (datetime.now(UTC) + timedelta(minutes=10)).isoformat()})
        return events("clock_drift")[-1]

    ntp.offset = None
    check(client)
    assert drift_event()["server_clock_ok"] is False
    with app.db.get_connection() as conn:
        conn.execute("DELETE FROM events WHERE event_type = 'clock_drift'")  # once a day per device
    ntp.offset = 0.0
    check(client)
    assert drift_event()["server_clock_ok"] is True


# === Settings ===


def login(client) -> None:
    with app.db.get_connection() as conn:
        app.db.set_config_value(conn, "parent_password_hash", hash_password(PASSWORD))
    assert client.post("/admin/login", data={"password": PASSWORD}).status_code == 200


def wait_for(predicate) -> None:
    """check_soon() runs after the request, on the app's loop."""
    for _ in range(100):
        if predicate():
            return
        time.sleep(0.01)
    raise AssertionError("not reached")


def test_status_shows_the_zone_and_the_clock(client, ntp):
    login(client)
    check(client)
    server = client.get("/admin/api/status").json()["server"]
    assert server["timezone"] == "Europe/Warsaw"
    assert server["ntp_server"] == "pool.ntp.org"
    assert server["clock"]["state"] == "ok"


def test_settings_change_the_zone_and_check_a_new_ntp_server(client, ntp):
    login(client)
    r = client.post("/admin/api/server_settings", json={"timezone": " America/New_York ", "ntp_server": "time.example"})
    assert r.json() == {"ok": True}
    assert (config("timezone"), config("ntp_server")) == ("America/New_York", "time.example")
    wait_for(lambda: ntp.asked and ntp.asked[-1] == "time.example")
    wait_for(lambda: client.get("/admin/api/status").json()["server"]["clock"]["server"] == "time.example")
    asked = len(ntp.asked)
    client.post("/admin/api/server_settings", json={"timezone": "Europe/Warsaw", "ntp_server": "time.example"})
    time.sleep(0.05)
    assert len(ntp.asked) == asked  # the same server: no new check
    client.post("/admin/api/server_settings", json={"timezone": "Europe/Warsaw", "ntp_server": ""})
    wait_for(lambda: client.get("/admin/api/status").json()["server"]["clock"]["state"] == "disabled")


@pytest.mark.parametrize("body", [
    {"timezone": "Europe/Nowhere", "ntp_server": ""},
    {"timezone": "", "ntp_server": ""},
    {"timezone": "UTC", "ntp_server": "time.example:123"},
    {"timezone": "UTC", "ntp_server": "-bad"},
    {"timezone": "UTC", "ntp_server": "a b"},
    {"timezone": "UTC", "ntp_server": "1:2:3"},
])
def test_settings_refuse_bad_values(client, body):
    login(client)
    assert client.post("/admin/api/server_settings", json=body).status_code == 422
    assert config("timezone") == "Europe/Warsaw"


@pytest.mark.parametrize("server", ["pool.ntp.org", "192.168.1.1", "2001:db8::1", "tempus1.gum.gov.pl", ""])
def test_settings_take_a_host_or_an_address(client, server):
    login(client)
    assert client.post("/admin/api/server_settings", json={"timezone": "UTC", "ntp_server": server}).json()["ok"]


def test_settings_need_a_login(client):
    assert client.post("/admin/api/server_settings", json={"timezone": "UTC"}).status_code == 401


# === The wizard ===


@pytest.fixture
def fresh(tmp_path, monkeypatch):
    monkeypatch.setattr(app.db, "DATABASE_PATH", tmp_path / "vigil.db")
    app.admin_auth._login_failures.clear()
    from app.main import app as fastapi_app

    with TestClient(fastapi_app) as c:
        yield c


WIZARD = {"child_name": "Ola", "password": PASSWORD, "timezone": "Europe/Warsaw"}


def test_wizard_saves_the_ntp_server(fresh, ntp):
    assert fresh.post("/setup/complete", json={**WIZARD, "ntp_server": "time.example"}).json() == {"ok": True}
    assert config("ntp_server") == "time.example"
    wait_for(lambda: ntp.asked and ntp.asked[-1] == "time.example")


def test_wizard_without_an_ntp_server_gets_the_default(fresh):
    assert fresh.post("/setup/complete", json=WIZARD).status_code == 200
    assert config("ntp_server") == "pool.ntp.org"


def test_wizard_reports_a_bad_ntp_server(fresh):
    r = fresh.post("/setup/complete", json={**WIZARD, "ntp_server": "time.example:123"})
    assert r.status_code == 422
    assert r.json()["detail"] == {"ntp_server": "invalid"}
