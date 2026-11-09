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
