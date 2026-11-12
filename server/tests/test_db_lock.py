"""Every write of a request holds get_db()'s lock (#123).

The /v1 nonce used to be written on a connection of its own, outside the lock: while a panel request held the
lock with its write not yet committed, the INSERT waited out SQLite's busy timeout inside the event loop, the
panel request could never commit, and the heartbeat failed with "database is locked" after 30 s.
"""

from __future__ import annotations

from conftest import Watchdog

import app.db

WRITES = ("INSERT", "UPDATE", "DELETE", "REPLACE")


def _trace_writes(monkeypatch) -> list[tuple[str, bool]]:
    seen: list[tuple[str, bool]] = []
    configure = app.db._configure_connection

    def traced(conn):
        configure(conn)
        conn.set_trace_callback(lambda sql: seen.append((sql.lstrip().split(" ", 1)[0].upper(),
                                                         app.db._db_lock.locked())))

    monkeypatch.setattr(app.db, "_configure_connection", traced)
    return seen


def test_v1_writes_hold_the_db_lock(client, watchdog, monkeypatch):
    seen = _trace_writes(monkeypatch)
    watchdog.heartbeat()
    assert watchdog.post("/v1/pause_toggle", {}).status_code == 200
    writes = [(sql, locked) for sql, locked in seen if sql in WRITES]
    assert any(sql == "INSERT" for sql, _ in writes)  # at least the nonces
    assert all(locked for _, locked in writes), writes


def test_nonce_is_burned_by_a_request_that_fails_later(client, watchdog):
    # The signature is valid but the body is not: the endpoint rejects it after authentication, and its
    # transaction rolls back — the nonce must stay used.
    r = watchdog.post("/v1/heartbeat", {"session_active": "not a bool"}, verify=False, nonce="n-late")
    assert r.status_code == 422
    r = Watchdog(client).post("/v1/heartbeat", {
        "session_active": True, "user_active": True, "current_time_utc": "2026-11-12T12:00:00+00:00"},
        verify=False, nonce="n-late")
    assert r.status_code == 401 and r.json()["detail"] == "duplicate_nonce"
