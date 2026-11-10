"""Schema migrations (migrations.py): fresh databases, reruns, downgrades, failures."""

import sqlite3
from contextlib import closing

import pytest

import app.db
from app.migrations import LATEST_VERSION


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
    tables = {r[0] for r in _query(db_path, "SELECT name FROM sqlite_master WHERE type = 'table'")}
    assert {"config", "children", "child_schedule", "devices", "child_daily", "events", "totp_lockout",
            "used_nonces"} <= tables
    assert {"acked_utc", "child_id", "device_id"} <= _columns(db_path, "events")


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


def test_v08_database_migrates(db_path):
    """A database of v0.8.0 (schema 1) gets the heartbeat remainder and loses the wizard's server address."""
    import app.migrations as migrations

    with closing(sqlite3.connect(db_path, isolation_level=None)) as conn:
        conn.execute("BEGIN")
        migrations.MIGRATIONS[0][1](conn)
        conn.execute("PRAGMA user_version = 1")
        conn.execute("INSERT INTO config VALUES ('watchdog_server_url', 'http://pc:8088')")
        conn.execute("INSERT INTO children(name, created_utc) VALUES ('Ola', '2026-11-01T00:00:00+00:00')")
        conn.execute("INSERT INTO devices(child_id, name, signing_key, paired_utc) VALUES (1, 'PC', 'k', 'x')")
        conn.execute("COMMIT")
    app.db.init_database()
    assert _query(db_path, "PRAGMA user_version") == [(LATEST_VERSION,)]
    assert _query(db_path, "SELECT delta_carry_ms FROM devices") == [(0,)]
    assert _query(db_path, "SELECT 1 FROM config WHERE key = 'watchdog_server_url'") == []
