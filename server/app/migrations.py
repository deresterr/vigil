"""Schema migrations. `PRAGMA user_version` = number of migrations applied.

Each migration runs in one transaction together with its user_version bump, so a failure leaves the
database at the previous version. Add new migrations at the end; never edit an applied one.

Databases from before migrations (v0.4, v0.5) have user_version 0 and already contain most of
migration 1, which is why that one alone is written to be idempotent.
"""

from __future__ import annotations

import logging
import sqlite3
from collections.abc import Callable

logger = logging.getLogger(__name__)

_BASELINE_SQL = (
    """CREATE TABLE IF NOT EXISTS config (
        key   TEXT PRIMARY KEY,
        value TEXT NOT NULL
    )""",
    # One row per local date. seconds_used in seconds: no float drift per tick.
    """CREATE TABLE IF NOT EXISTS daily_state (
        date_local                 TEXT PRIMARY KEY,
        seconds_used               INTEGER NOT NULL DEFAULT 0,
        limit_override_minutes     INTEGER NOT NULL DEFAULT 0,
        night_extension_until_utc  TEXT,
        manual_lock                INTEGER NOT NULL DEFAULT 0,
        pause_active               INTEGER NOT NULL DEFAULT 0,
        last_heartbeat_utc         TEXT,
        last_session_active_utc    TEXT
    )""",
    """CREATE TABLE IF NOT EXISTS events (
        id           INTEGER PRIMARY KEY AUTOINCREMENT,
        ts_utc       TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
        event_type   TEXT NOT NULL,
        details_json TEXT,
        acked_utc    TEXT
    )""",
    "CREATE INDEX IF NOT EXISTS idx_events_ts ON events(ts_utc)",
    "CREATE INDEX IF NOT EXISTS idx_events_type ON events(event_type)",
    # Single row (id = 1): TOTP brute-force lockout.
    """CREATE TABLE IF NOT EXISTS totp_lockout (
        id                  INTEGER PRIMARY KEY CHECK (id = 1),
        attempts_count      INTEGER NOT NULL DEFAULT 0,
        first_attempt_utc   TEXT,
        lockout_until_utc   TEXT
    )""",
    """CREATE TABLE IF NOT EXISTS used_nonces (
        nonce  TEXT PRIMARY KEY,
        ts_utc TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP
    )""",
    "CREATE INDEX IF NOT EXISTS idx_nonces_ts ON used_nonces(ts_utc)",
)


def _columns(conn: sqlite3.Connection, table: str) -> set[str]:
    return {row[1] for row in conn.execute(f"PRAGMA table_info({table})")}


def _m001_baseline(conn: sqlite3.Connection) -> None:
    """The v0.5 schema, reached from nothing, v0.4 or v0.5."""
    for statement in _BASELINE_SQL:
        conn.execute(statement)
    if "pause_active" not in _columns(conn, "daily_state"):  # before pause existed
        conn.execute("ALTER TABLE daily_state ADD COLUMN pause_active INTEGER NOT NULL DEFAULT 0")
    if "acked_utc" not in _columns(conn, "events"):  # v0.4: alerts are acknowledged since #8
        conn.execute("ALTER TABLE events ADD COLUMN acked_utc TEXT")
    # v0.4 secrets that no longer exist: bearer token (protocol v2, #3), open init mode (pairing, #4).
    conn.execute("DELETE FROM config WHERE key IN ('bearer_token', 'init_open')")


MIGRATIONS: list[tuple[str, Callable[[sqlite3.Connection], None]]] = [
    ("baseline v0.5", _m001_baseline),
]

LATEST_VERSION = len(MIGRATIONS)


def migrate(conn: sqlite3.Connection) -> int:
    """Brings the database to LATEST_VERSION; returns it.

    Refuses a database newer than this code (a downgrade would silently misread it).
    The connection must be in autocommit mode (isolation_level None): transactions are explicit.
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
        logger.info("Schema migrated to %d (%s)", version, name)
    return LATEST_VERSION
