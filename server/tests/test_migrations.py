"""Schema migrations (migrations.py): fresh databases, pre-migration installs, downgrades."""

import sqlite3
from contextlib import closing

import pytest

import app.db
from app.migrations import LATEST_VERSION

# Schema of a v0.4 install (production): no events.acked_utc, user_version 0.
V04_SCHEMA = """
CREATE TABLE config (key TEXT PRIMARY KEY, value TEXT NOT NULL);
CREATE TABLE daily_state (
    date_local TEXT PRIMARY KEY, seconds_used INTEGER NOT NULL DEFAULT 0,
    limit_override_minutes INTEGER NOT NULL DEFAULT 0, night_extension_until_utc TEXT,
    manual_lock INTEGER NOT NULL DEFAULT 0, pause_active INTEGER NOT NULL DEFAULT 0,
    last_heartbeat_utc TEXT, last_session_active_utc TEXT);
CREATE TABLE events (id INTEGER PRIMARY KEY AUTOINCREMENT, ts_utc TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
    event_type TEXT NOT NULL, details_json TEXT);
CREATE TABLE totp_lockout (id INTEGER PRIMARY KEY CHECK (id = 1), attempts_count INTEGER NOT NULL DEFAULT 0,
    first_attempt_utc TEXT, lockout_until_utc TEXT);
CREATE TABLE used_nonces (nonce TEXT PRIMARY KEY, ts_utc TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP);
INSERT INTO config VALUES ('signing_key', 'k'), ('bearer_token', 'old'), ('daily_limit_weekday', '90'),
    ('setup_complete', '1'), ('device_name', 'Komputer Jasia'), ('timezone', 'Europe/Warsaw'),
    ('wd_instance_id', 'abc'), ('wd_awake_ms', '5000'), ('last_lock_reason', 'limit');
INSERT INTO daily_state(date_local, seconds_used, manual_lock, last_heartbeat_utc, last_session_active_utc)
    VALUES ('2026-11-01', 3600, 1, '2026-11-01T18:00:00+00:00', '2026-11-01T17:59:55+00:00');
INSERT INTO events(event_type) VALUES ('vigil_started');
INSERT INTO events(event_type) VALUES ('watchdog_started');
"""


@pytest.fixture
def db_path(tmp_path, monkeypatch):
    path = tmp_path / "vigil.db"
    monkeypatch.setattr(app.db, "DATABASE_PATH", path)
    return path


def _query(path, sql):
    with closing(sqlite3.connect(path)) as conn:
        return conn.execute(sql).fetchall()


def _columns(path, table):
    return {row[1] for row in _query(path, f"PRAGMA table_info({table})")}


def test_fresh_database_gets_the_latest_schema(db_path):
    app.db.init_database()
    assert _query(db_path, "PRAGMA user_version") == [(LATEST_VERSION,)]
    assert "acked_utc" in _columns(db_path, "events")


def test_v04_database_is_migrated_with_its_data(db_path):
    with closing(sqlite3.connect(db_path)) as conn:
        conn.executescript(V04_SCHEMA)

    app.db.init_database()

    assert _query(db_path, "PRAGMA user_version") == [(LATEST_VERSION,)]
    assert "acked_utc" in _columns(db_path, "events")
    # Per-child and per-device keys left config; bearer_token is gone, the rest stays.
    assert _query(db_path, "SELECT key, value FROM config ORDER BY key") == [
        ("setup_complete", "1"), ("timezone", "Europe/Warsaw")]
    # D7: the one PC became child 1 with device 1, settings and state carried over.
    assert _query(db_path, "SELECT id, name, last_lock_reason FROM children") == [(1, "Komputer Jasia", "limit")]
    # #48: the week as it was — v0.4 had weekday 90 (weekend and night: defaults), Friday counted as weekend.
    assert _query(db_path, "SELECT weekday, daily_limit, night_start, night_end FROM child_schedule "
                           "WHERE child_id = 1 ORDER BY weekday") == (
        [(d, 90, "21:30", "06:30") for d in range(4)] + [(d, 240, "21:30", "06:30") for d in range(4, 7)])
    assert _query(db_path, "SELECT id, child_id, signing_key, last_heartbeat_utc, wd_instance_id, wd_awake_ms "
                           "FROM devices") == [(1, 1, "k", "2026-11-01T18:00:00+00:00", "abc", 5000)]
    assert _query(db_path, "SELECT child_id, date_local, seconds_used, manual_lock FROM child_daily") == [
        (1, "2026-11-01", 3600, 1)]
    assert _query(db_path, "SELECT event_type, child_id, device_id FROM events ORDER BY id") == [
        ("vigil_started", 1, None), ("watchdog_started", 1, 1)]
    assert _query(db_path, "SELECT name FROM sqlite_master WHERE name = 'daily_state'") == []


def test_database_before_setup_gets_no_child(db_path):
    with closing(sqlite3.connect(db_path)) as conn:
        conn.executescript(V04_SCHEMA)
        conn.execute("UPDATE config SET value = '0' WHERE key = 'setup_complete'")
        conn.commit()
    app.db.init_database()
    assert _query(db_path, "SELECT COUNT(*) FROM children") == [(0,)]
    assert _query(db_path, "SELECT COUNT(*) FROM devices") == [(0,)]


def test_pc_that_never_connected_is_not_a_device(db_path):
    """bootstrap generated a key before any pairing; a PC that never sent a heartbeat is not a device."""
    with closing(sqlite3.connect(db_path)) as conn:
        conn.executescript(V04_SCHEMA)
        conn.execute("UPDATE daily_state SET last_heartbeat_utc = NULL")
        conn.execute("DELETE FROM config WHERE key = 'wd_instance_id'")
        conn.commit()
    app.db.init_database()
    assert _query(db_path, "SELECT COUNT(*) FROM children") == [(1,)]
    assert _query(db_path, "SELECT COUNT(*) FROM devices") == [(0,)]


def test_migration_is_idempotent(db_path):
    app.db.init_database()
    app.db.init_database()
    assert _query(db_path, "PRAGMA user_version") == [(LATEST_VERSION,)]


def test_database_newer_than_the_server_is_refused(db_path):
    app.db.init_database()
    with closing(sqlite3.connect(db_path)) as conn:
        conn.execute(f"PRAGMA user_version = {LATEST_VERSION + 1}")
    with pytest.raises(RuntimeError, match="newer than this server"):
        app.db.init_database()


def test_failed_migration_leaves_the_previous_version(db_path, monkeypatch):
    import app.migrations as migrations

    def boom(conn):
        conn.execute("CREATE TABLE half_done (x INTEGER)")
        raise ValueError("boom")

    monkeypatch.setattr(migrations, "MIGRATIONS", [*migrations.MIGRATIONS, ("broken", boom)])
    monkeypatch.setattr(migrations, "LATEST_VERSION", LATEST_VERSION + 1)
    with pytest.raises(ValueError):
        app.db.init_database()
    assert _query(db_path, "PRAGMA user_version") == [(LATEST_VERSION,)]
    assert _query(db_path, "SELECT name FROM sqlite_master WHERE name = 'half_done'") == []


def test_d7_children_get_their_week(db_path, monkeypatch):
    """Migration 3 (#48) from a schema-2 database: each child's two limits and night window become 7 days."""
    import app.migrations as migrations

    with monkeypatch.context() as m:
        m.setattr(migrations, "MIGRATIONS", migrations.MIGRATIONS[:2])
        m.setattr(migrations, "LATEST_VERSION", 2)
        app.db.init_database()
    with closing(sqlite3.connect(db_path)) as conn:
        conn.executemany(
            "INSERT INTO children(name, daily_limit_weekday, daily_limit_weekend, night_lock_start, night_lock_end, "
            "created_utc) VALUES (?, ?, ?, ?, ?, '2026-11-08')",
            [("Ola", 60, 180, "22:00", "07:00"), ("Kuba", 0, 1440, "00:30", "06:00")])
        conn.commit()

    app.db.init_database()

    assert _query(db_path, "PRAGMA user_version") == [(LATEST_VERSION,)]
    assert {"daily_limit_weekday", "night_lock_start"} & _columns(db_path, "children") == set()
    rows = _query(db_path, "SELECT child_id, weekday, daily_limit, night_start, night_end FROM child_schedule "
                           "ORDER BY child_id, weekday")
    assert rows == ([(1, d, 60 if d < 4 else 180, "22:00", "07:00") for d in range(7)]
                    + [(2, d, 0 if d < 4 else 1440, "00:30", "06:00") for d in range(7)])
