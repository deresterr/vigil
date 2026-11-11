"""Schema migrations. `PRAGMA user_version` = number of migrations applied.

Each migration runs in one transaction together with its user_version bump, so a failure leaves the
database at the previous version. Add new migrations at the end; never edit an applied one.

Migration 1 is the schema of v0.8 (D26: the migrations of earlier versions were folded into it, as no
database of theirs is migrated).
"""

from __future__ import annotations

import logging
import sqlite3
from collections.abc import Callable

logger = logging.getLogger(__name__)

_BASELINE_SQL = (
    """CREATE TABLE config (
        key   TEXT PRIMARY KEY,
        value TEXT NOT NULL
    )""",
    """CREATE TABLE children (
        id                INTEGER PRIMARY KEY,
        name              TEXT NOT NULL,
        last_lock_reason  TEXT NOT NULL DEFAULT '',  -- lock_started is logged once per child, not per device
        created_utc       TEXT NOT NULL
    )""",
    # The week (D19): a daily limit and a night window per weekday. A night belongs to the evening of its
    # day (lock_logic.night_window).
    """CREATE TABLE child_schedule (
        child_id     INTEGER NOT NULL REFERENCES children(id),
        weekday      INTEGER NOT NULL CHECK (weekday BETWEEN 0 AND 6),  -- Monday = 0 (date.weekday())
        daily_limit  INTEGER NOT NULL,
        night_start  TEXT NOT NULL,
        night_end    TEXT NOT NULL,
        PRIMARY KEY (child_id, weekday)
    )""",
    # Identified by its signing key (auth.py tries each); deleting the row unpairs the PC.
    """CREATE TABLE devices (
        id                  INTEGER PRIMARY KEY,
        child_id            INTEGER NOT NULL REFERENCES children(id),
        name                TEXT NOT NULL,
        signing_key         TEXT NOT NULL UNIQUE,
        paired_utc          TEXT NOT NULL,
        last_heartbeat_utc  TEXT,
        last_active_utc     TEXT,
        seconds_today       INTEGER NOT NULL DEFAULT 0,  -- this device's share of the child's time
        seconds_date        TEXT,                        -- local date seconds_today belongs to
        wd_instance_id      TEXT NOT NULL DEFAULT '',    -- tamper baseline (tamper.py)
        wd_awake_ms         INTEGER NOT NULL DEFAULT -1,
        wd_goodbye          TEXT NOT NULL DEFAULT ''
    )""",
    "CREATE INDEX idx_devices_child ON devices(child_id)",
    # One row per child and local date. seconds_used in seconds: no float drift per tick.
    """CREATE TABLE child_daily (
        child_id                   INTEGER NOT NULL REFERENCES children(id),
        date_local                 TEXT NOT NULL,
        seconds_used               INTEGER NOT NULL DEFAULT 0,
        limit_override_minutes     INTEGER NOT NULL DEFAULT 0,
        night_extension_until_utc  TEXT,
        manual_lock                INTEGER NOT NULL DEFAULT 0,
        pause_active               INTEGER NOT NULL DEFAULT 0,
        PRIMARY KEY (child_id, date_local)
    )""",
    """CREATE TABLE events (
        id           INTEGER PRIMARY KEY AUTOINCREMENT,
        ts_utc       TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
        event_type   TEXT NOT NULL,
        details_json TEXT,
        acked_utc    TEXT,
        child_id     INTEGER,
        device_id    INTEGER
    )""",
    "CREATE INDEX idx_events_ts ON events(ts_utc)",
    "CREATE INDEX idx_events_type ON events(event_type)",
    "CREATE INDEX idx_events_child ON events(child_id)",
    # Single row (id = 1): TOTP brute-force lockout, one TOTP secret for all children.
    """CREATE TABLE totp_lockout (
        id                  INTEGER PRIMARY KEY CHECK (id = 1),
        attempts_count      INTEGER NOT NULL DEFAULT 0,
        first_attempt_utc   TEXT,
        lockout_until_utc   TEXT
    )""",
    """CREATE TABLE used_nonces (
        nonce  TEXT PRIMARY KEY,
        ts_utc TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP
    )""",
    "CREATE INDEX idx_nonces_ts ON used_nonces(ts_utc)",
)


def _m001_baseline(conn: sqlite3.Connection) -> None:
    """The v0.8 schema."""
    for statement in _BASELINE_SQL:
        conn.execute(statement)


def _m002_delta_carry(conn: sqlite3.Connection) -> None:
    """The fraction of a second each heartbeat leaves over (heartbeat.py), and the wizard's dropped server
    address (#86)."""
    conn.execute("ALTER TABLE devices ADD COLUMN delta_carry_ms INTEGER NOT NULL DEFAULT 0")
    conn.execute("DELETE FROM config WHERE key = 'watchdog_server_url'")


MIGRATIONS: list[tuple[str, Callable[[sqlite3.Connection], None]]] = [
    ("baseline v0.8", _m001_baseline),
    ("devices.delta_carry_ms", _m002_delta_carry),
]

LATEST_VERSION = len(MIGRATIONS)


def migrate(conn: sqlite3.Connection) -> int:
    """Brings the database to LATEST_VERSION; returns it.

    Refuses a database newer than this code (a downgrade would silently misread it).
    The connection must be in autocommit mode (isolation_level None): transactions are explicit.
    Each migration of an existing database is logged; a new one (version 0) is just "new" in the start line.
    """
    current = conn.execute("PRAGMA user_version").fetchone()[0]
    if current > LATEST_VERSION:
        raise RuntimeError(
            f"Database schema version {current} is newer than this server ({LATEST_VERSION}); "
            "run the newer server or restore a backup")
    for version in range(current + 1, LATEST_VERSION + 1):
        name, apply = MIGRATIONS[version - 1]
        conn.execute("BEGIN IMMEDIATE")
        try:
            apply(conn)
            conn.execute(f"PRAGMA user_version = {version}")
            conn.execute("COMMIT")
        except BaseException:
            conn.execute("ROLLBACK")
            raise
        if current:
            logger.info("Database migrated to schema %d (%s)", version, name)
    return LATEST_VERSION
