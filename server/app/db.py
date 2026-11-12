"""SQLite: connections, schema setup, query helpers.

No ORM on purpose: plain SQL, `?` parameters, sqlite3.Row. One uvicorn worker with WAL, but asyncio still
interleaves requests between awaits, so get_db() holds an asyncio.Lock: one request at a time touches the
database (the time accounting relies on it, CLAUDE.md).

One server, many children (D7): per-child settings in `children`, devices with their signing keys
in `devices`, per-child days in `child_daily`. Server-wide settings and secrets (totp_secret,
parent_password_hash, admin_session_key) are single keys in `config`. Schema: migrations.py.
"""

from __future__ import annotations

import asyncio
import json
import logging
import sqlite3
from collections.abc import AsyncGenerator, Generator
from contextlib import closing, contextmanager
from datetime import date, datetime, timezone

from .config import CONFIG_DEFAULTS, DATABASE_PATH
from .migrations import migrate

logger = logging.getLogger(__name__)


def _configure_connection(conn: sqlite3.Connection) -> None:
    """Pragmas and row factory of every new connection."""
    conn.row_factory = sqlite3.Row
    conn.execute("PRAGMA busy_timeout = 30000")  # 30 s


def init_database() -> int:
    """WAL mode + schema migrations (migrations.py). Idempotent, runs at every start. Returns the schema
    version."""
    DATABASE_PATH.parent.mkdir(parents=True, exist_ok=True)

    # isolation_level None: migrate() manages its own transactions.
    with closing(sqlite3.connect(DATABASE_PATH, isolation_level=None)) as conn:
        # WAL is persistent in the file, but a fresh file or a restored backup may not have it.
        mode = conn.execute("PRAGMA journal_mode = WAL").fetchone()[0]
        if mode.lower() != "wal":
            logger.warning("Could not enable WAL mode, current: %s", mode)
        conn.execute("PRAGMA synchronous = NORMAL")  # WAL + NORMAL: safe and fast
        return migrate(conn)


@contextmanager
def get_connection() -> Generator[sqlite3.Connection, None, None]:
    """One connection: commit when the block ends normally, rollback on an exception.

    `check_same_thread=False`: FastAPI moves a request between threads (sync endpoints in its threadpool,
    middleware on another thread than dependencies); access is serialized by get_db's lock anyway.
    `timeout=30`: SQLite's busy retry, a safety net next to that lock.
    """
    conn = sqlite3.connect(DATABASE_PATH, check_same_thread=False, timeout=30.0)
    _configure_connection(conn)
    try:
        yield conn
        conn.commit()
    except Exception:
        conn.rollback()
        raise
    finally:
        conn.close()


# Serializes every request's database access: heartbeats, pause toggles and panel calls interleave in one
# asyncio loop. With one uvicorn worker this is the only layer needed; WAL and the timeout are a safety net.
# Every write in a request goes through get_db (#123): a write on a connection of its own waits for SQLite's
# lock synchronously, in the event loop, so the request holding it can never commit. Only startup and
# read-only checks (setup_guard, "/") use get_connection directly; WAL readers never wait for a writer.
_db_lock = asyncio.Lock()


async def get_db() -> AsyncGenerator[sqlite3.Connection, None]:
    """FastAPI dependency: one request at a time (asyncio.Lock)."""
    async with _db_lock:
        with get_connection() as conn:
            yield conn


# === Query helpers ===


def get_config_value(conn: sqlite3.Connection, key: str) -> str | None:
    row = conn.execute("SELECT value FROM config WHERE key = ?", (key,)).fetchone()
    return row["value"] if row else None


def get_config_values(conn: sqlite3.Connection, keys: tuple[str, ...]) -> dict[str, str]:
    """Several config keys in one SELECT; a missing row falls back to CONFIG_DEFAULTS."""
    placeholders = ",".join("?" * len(keys))
    rows = dict(conn.execute(f"SELECT key, value FROM config WHERE key IN ({placeholders})", keys).fetchall())
    return {k: rows.get(k, CONFIG_DEFAULTS.get(k, "")) for k in keys}


def get_config_int(conn: sqlite3.Connection, key: str) -> int:
    val = get_config_value(conn, key)
    if val is None:
        raise RuntimeError(f"No config value for {key!r}")
    return int(val)


def set_config_value(conn: sqlite3.Connection, key: str, value: str | int) -> None:
    conn.execute(
        "INSERT INTO config(key, value) VALUES (?, ?) "
        "ON CONFLICT(key) DO UPDATE SET value = excluded.value",
        (key, str(value)),
    )


def log_event(
    conn: sqlite3.Connection,
    event_type: str,
    details: dict | None = None,
    ts_utc: datetime | None = None,
    *,
    child_id: int | None = None,
    device_id: int | None = None,
) -> None:
    """One row in `events`; details as JSON when not empty.

    ts_utc is written as Python isoformat (with 'T' and offset): SQLite CURRENT_TIMESTAMP uses a space
    and would not compare lexicographically with our cutoffs. child_id/device_id say whom it concerns
    (journal filter and labels); parent-wide events (password, TOTP) carry neither.
    """
    payload = json.dumps(details, separators=(",", ":"), default=str) if details else None
    conn.execute(
        "INSERT INTO events(event_type, details_json, ts_utc, child_id, device_id) VALUES (?, ?, ?, ?, ?)",
        (event_type, payload, (ts_utc or datetime.now(timezone.utc)).isoformat(), child_id, device_id),
    )


# === Children and their days ===


def get_child(conn: sqlite3.Connection, child_id: int) -> sqlite3.Row | None:
    return conn.execute("SELECT * FROM children WHERE id = ?", (child_id,)).fetchone()


def ensure_child_day(conn: sqlite3.Connection, child_id: int, date_local: date) -> sqlite3.Row:
    """The child's child_daily row for this date, created when missing.

    Fast path: the row exists (all day long) — only a SELECT, no write lock.
    Slow path: the first request of the date inserts it. State not tied to a calendar day is carried
    over from the child's most recent previous row:
    - manual_lock: stays until the parent removes it,
    - night_extension_until_utc: a night window spans midnight, so a grant made before midnight
      must still apply after it (only carried while it is still in the future).
    """
    key = (child_id, date_local.isoformat())
    row = conn.execute("SELECT * FROM child_daily WHERE child_id = ? AND date_local = ?", key).fetchone()
    if row is not None:
        return row
    conn.execute("INSERT INTO child_daily(child_id, date_local) VALUES (?, ?) ON CONFLICT DO NOTHING", key)
    prev = conn.execute(
        "SELECT manual_lock, night_extension_until_utc FROM child_daily WHERE child_id = ? AND date_local < ? "
        "ORDER BY date_local DESC LIMIT 1",
        key,
    ).fetchone()
    if prev:
        carried: dict = {}
        if prev["manual_lock"]:
            carried["manual_lock"] = 1
        ext = prev["night_extension_until_utc"]
        if ext and datetime.fromisoformat(ext.replace("Z", "+00:00")) > datetime.now(timezone.utc):
            carried["night_extension_until_utc"] = ext
        update_child_day(conn, child_id, date_local, **carried)
    return conn.execute("SELECT * FROM child_daily WHERE child_id = ? AND date_local = ?", key).fetchone()


def update_child_day(conn: sqlite3.Connection, child_id: int, date_local: date, **fields) -> None:
    """UPDATE of the given columns; column names come from our code only, values are parameters."""
    if not fields:
        return
    set_clause = ", ".join(f"{k} = ?" for k in fields)
    conn.execute(
        f"UPDATE child_daily SET {set_clause} WHERE child_id = ? AND date_local = ?",
        [*fields.values(), child_id, date_local.isoformat()],
    )
