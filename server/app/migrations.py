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
from datetime import datetime, timezone

from .config import CHILD_DEFAULTS

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


# Events written by a device (watchdog); the rest are child- or parent-level. Used to attribute the
# events of a pre-D7 database to its one device.
_DEVICE_EVENT_TYPES = ("pause_toggled", "offline_unlock", "clock_drift", "totp_failed", "totp_success",
                       "device_paired")


def _m002_children_devices(conn: sqlite3.Connection) -> None:
    """D7 (#40): one server, many children, each with devices. Time and locks are per child.

    A configured single-PC database becomes child 1 (named after its device_name) with device 1 (its
    signing key, if the PC ever connected). Per-child config keys move into `children`, the device's
    tamper state into `devices`, `daily_state` into `child_daily`.
    """
    conn.execute("""CREATE TABLE children (
        id                   INTEGER PRIMARY KEY,
        name                 TEXT NOT NULL,
        daily_limit_weekday  INTEGER NOT NULL,
        daily_limit_weekend  INTEGER NOT NULL,
        night_lock_start     TEXT NOT NULL,
        night_lock_end       TEXT NOT NULL,
        last_lock_reason     TEXT NOT NULL DEFAULT '',
        created_utc          TEXT NOT NULL
    )""")
    # Identified by its signing key (auth.py tries each); deleting the row unpairs the PC.
    conn.execute("""CREATE TABLE devices (
        id                  INTEGER PRIMARY KEY,
        child_id            INTEGER NOT NULL REFERENCES children(id),
        name                TEXT NOT NULL,
        signing_key         TEXT NOT NULL UNIQUE,
        paired_utc          TEXT NOT NULL,
        last_heartbeat_utc  TEXT,
        last_active_utc     TEXT,
        seconds_today       INTEGER NOT NULL DEFAULT 0,  -- this device's share of the child's time
        seconds_date        TEXT,                        -- local date seconds_today belongs to
        wd_instance_id      TEXT NOT NULL DEFAULT '',
        wd_awake_ms         INTEGER NOT NULL DEFAULT -1,
        wd_goodbye          TEXT NOT NULL DEFAULT ''
    )""")
    conn.execute("CREATE INDEX idx_devices_child ON devices(child_id)")
    conn.execute("""CREATE TABLE child_daily (
        child_id                   INTEGER NOT NULL REFERENCES children(id),
        date_local                 TEXT NOT NULL,
        seconds_used               INTEGER NOT NULL DEFAULT 0,
        limit_override_minutes     INTEGER NOT NULL DEFAULT 0,
        night_extension_until_utc  TEXT,
        manual_lock                INTEGER NOT NULL DEFAULT 0,
        pause_active               INTEGER NOT NULL DEFAULT 0,
        PRIMARY KEY (child_id, date_local)
    )""")
    conn.execute("ALTER TABLE events ADD COLUMN child_id INTEGER")
    conn.execute("ALTER TABLE events ADD COLUMN device_id INTEGER")
    conn.execute("CREATE INDEX idx_events_child ON events(child_id)")

    cfg = dict(conn.execute("SELECT key, value FROM config").fetchall())
    if cfg.get("setup_complete") == "1":
        now = datetime.now(timezone.utc).isoformat()
        conn.execute(
            "INSERT INTO children(id, name, daily_limit_weekday, daily_limit_weekend, night_lock_start, "
            "night_lock_end, last_lock_reason, created_utc) VALUES (1, ?, ?, ?, ?, ?, ?, ?)",
            ((cfg.get("device_name") or "").strip() or "Dziecko",
             int(cfg.get("daily_limit_weekday") or CHILD_DEFAULTS["daily_limit_weekday"]),
             int(cfg.get("daily_limit_weekend") or CHILD_DEFAULTS["daily_limit_weekend"]),
             cfg.get("night_lock_start") or CHILD_DEFAULTS["night_lock_start"],
             cfg.get("night_lock_end") or CHILD_DEFAULTS["night_lock_end"],
             cfg.get("last_lock_reason", ""), now))
        conn.execute(
            "INSERT INTO child_daily(child_id, date_local, seconds_used, limit_override_minutes, "
            "night_extension_until_utc, manual_lock, pause_active) SELECT 1, date_local, seconds_used, "
            "limit_override_minutes, night_extension_until_utc, manual_lock, pause_active FROM daily_state")
        last = conn.execute(
            "SELECT last_heartbeat_utc, last_session_active_utc FROM daily_state "
            "WHERE last_heartbeat_utc IS NOT NULL ORDER BY date_local DESC LIMIT 1").fetchone()
        # bootstrap generated a key before any pairing: only a PC that connected becomes a device.
        if cfg.get("signing_key") and (last or cfg.get("wd_instance_id")):
            conn.execute(
                "INSERT INTO devices(id, child_id, name, signing_key, paired_utc, last_heartbeat_utc, "
                "last_active_utc, wd_instance_id, wd_awake_ms, wd_goodbye) VALUES (1, 1, ?, ?, ?, ?, ?, ?, ?, ?)",
                ("Komputer", cfg["signing_key"], now, last[0] if last else None, last[1] if last else None,
                 cfg.get("wd_instance_id", ""), int(cfg.get("wd_awake_ms") or -1), cfg.get("wd_goodbye", "")))
            types = ",".join("?" * len(_DEVICE_EVENT_TYPES))
            conn.execute(
                f"UPDATE events SET device_id = 1 WHERE event_type LIKE 'watchdog\\_%' ESCAPE '\\' "
                f"OR event_type LIKE 'tamper\\_%' ESCAPE '\\' OR event_type IN ({types})",
                _DEVICE_EVENT_TYPES)
        conn.execute("UPDATE events SET child_id = 1")

    conn.execute("DROP TABLE daily_state")
    conn.execute(
        "DELETE FROM config WHERE key IN ('device_name', 'daily_limit_weekday', 'daily_limit_weekend', "
        "'night_lock_start', 'night_lock_end', 'last_lock_reason', 'signing_key', 'wd_instance_id', "
        "'wd_awake_ms', 'wd_goodbye')")


MIGRATIONS: list[tuple[str, Callable[[sqlite3.Connection], None]]] = [
    ("baseline v0.5", _m001_baseline),
    ("children and devices (D7)", _m002_children_devices),
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
