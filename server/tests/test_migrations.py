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
INSERT INTO config VALUES ('signing_key', 'k'), ('bearer_token', 'old'), ('daily_limit_weekday', '90');
INSERT INTO daily_state(date_local, seconds_used) VALUES ('2026-11-01', 3600);
INSERT INTO events(event_type) VALUES ('vigil_started');
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
    assert _query(db_path, "SELECT key, value FROM config ORDER BY key") == [
        ("daily_limit_weekday", "90"), ("signing_key", "k")]  # bearer_token dropped, the rest kept
    assert _query(db_path, "SELECT seconds_used FROM daily_state") == [(3600,)]
    assert _query(db_path, "SELECT event_type FROM events") == [("vigil_started",)]


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
