"""Warstwa SQLite: connection management + schema bootstrap + query helpers.

Świadomy wybór: bez ORM. Ręczny SQL, parametry przez `?`, sqlite3.Row dla dict-like access.
Single-worker uvicorn + WAL mode — ale asyncio nadal obsługuje kilka requestów współbieżnie
(między await-ami). Dlatego get_db() używa asyncio.Lock: jeden request naraz ma dostęp do DB.

Single-tenant: jeden PC dziecka = jeden kontener. Sekrety (bearer_token, signing_key,
totp_secret, parent_password_hash) trzymane w `config` table jako pojedyncze klucze.
"""

from __future__ import annotations

import asyncio
import json
import logging
import sqlite3
from collections.abc import AsyncGenerator, Generator
from contextlib import contextmanager
from datetime import date, datetime, timezone

from .config import DATABASE_PATH

logger = logging.getLogger(__name__)


SCHEMA_SQL = """
CREATE TABLE IF NOT EXISTS config (
    key   TEXT PRIMARY KEY,
    value TEXT NOT NULL
);

-- daily_state: jeden rekord per dzień (jeden klient w całym systemie).
-- seconds_used trzymany jako INTEGER w sekundach (unika floating-point przy każdym tick).
-- Konwersja na minuty robi się dopiero przy budowaniu response (seconds_used // 60).
CREATE TABLE IF NOT EXISTS daily_state (
    date_local                 TEXT PRIMARY KEY,
    seconds_used               INTEGER NOT NULL DEFAULT 0,
    limit_override_minutes     INTEGER NOT NULL DEFAULT 0,
    night_extension_until_utc  TEXT,
    manual_lock                INTEGER NOT NULL DEFAULT 0,
    pause_active               INTEGER NOT NULL DEFAULT 0,
    last_heartbeat_utc         TEXT,
    last_session_active_utc    TEXT
);

CREATE TABLE IF NOT EXISTS events (
    id           INTEGER PRIMARY KEY AUTOINCREMENT,
    ts_utc       TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
    event_type   TEXT NOT NULL,
    details_json TEXT
);
CREATE INDEX IF NOT EXISTS idx_events_ts ON events(ts_utc);
CREATE INDEX IF NOT EXISTS idx_events_type ON events(event_type);

-- totp_lockout: jeden rekord (id=1) — TOTP rate-limiting po brute-force próbach.
CREATE TABLE IF NOT EXISTS totp_lockout (
    id                  INTEGER PRIMARY KEY CHECK (id = 1),
    attempts_count      INTEGER NOT NULL DEFAULT 0,
    first_attempt_utc   TEXT,
    lockout_until_utc   TEXT
);

CREATE TABLE IF NOT EXISTS used_nonces (
    nonce  TEXT PRIMARY KEY,
    ts_utc TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP
);
CREATE INDEX IF NOT EXISTS idx_nonces_ts ON used_nonces(ts_utc);
"""


def _configure_connection(conn: sqlite3.Connection) -> None:
    """Pragmy i row factory na każde nowe połączenie."""
    conn.row_factory = sqlite3.Row
    conn.execute("PRAGMA busy_timeout = 30000")  # 30s — wystarcza dla burst kliknięć w admin UI


def init_database() -> None:
    """Tworzy schemat i ustawia WAL mode. Idempotent — można wołać przy każdym starcie."""
    DATABASE_PATH.parent.mkdir(parents=True, exist_ok=True)

    with sqlite3.connect(DATABASE_PATH, check_same_thread=False) as conn:
        # WAL mode jest persistent (zapisany w pliku DB), ale wołamy przy każdym starcie
        # na wypadek świeżego pliku lub przywróconego z backupu w innym mode.
        mode = conn.execute("PRAGMA journal_mode = WAL").fetchone()[0]
        if mode.lower() != "wal":
            logger.warning("Nie udało się ustawić WAL mode, aktualnie: %s", mode)

        conn.execute("PRAGMA synchronous = NORMAL")  # WAL + NORMAL = bezpieczne i szybkie
        conn.executescript(SCHEMA_SQL)
        # Migracja: dodaj pause_active jeśli nie istnieje (istniejące bazy bez tej kolumny)
        try:
            conn.execute(
                "ALTER TABLE daily_state ADD COLUMN pause_active INTEGER NOT NULL DEFAULT 0"
            )
        except sqlite3.OperationalError:
            pass  # kolumna już istnieje
        conn.commit()

    logger.info("Schemat DB zainicjalizowany (%s)", DATABASE_PATH)


@contextmanager
def get_connection() -> Generator[sqlite3.Connection, None, None]:
    """Context manager dla jednego połączenia. Auto-commit przy wyjściu bez exception, rollback przy exception.

    `check_same_thread=False`: FastAPI dispatchuje request między wątkami (sync endpoints w threadpool,
    BaseHTTPMiddleware w innym wątku niż dependencies). WAL mode + GIL = bezpieczne dla naszego use case'u.

    `timeout=30`: Python-level busy retry przy SQLITE_BUSY — belt-and-suspenders obok asyncio.Lock w get_db().
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


# asyncio.Lock serializes wszystkie requesty do DB — zapobiega "database is locked" gdy
# heartbeat i pause_toggle / admin API trafiają jednocześnie w tę samą pętlę asyncio.
# Przy single-worker uvicorn to jedyna warstwa potrzebna; WAL + timeout jako safety net.
_db_lock = asyncio.Lock()


async def get_db() -> AsyncGenerator[sqlite3.Connection, None]:
    """FastAPI dependency. Jeden request na raz przez asyncio.Lock."""
    async with _db_lock:
        with get_connection() as conn:
            yield conn


# === Query helpers ===


def get_config_value(conn: sqlite3.Connection, key: str) -> str | None:
    row = conn.execute("SELECT value FROM config WHERE key = ?", (key,)).fetchone()
    return row["value"] if row else None


def get_config_int(conn: sqlite3.Connection, key: str) -> int:
    val = get_config_value(conn, key)
    if val is None:
        raise RuntimeError(f"Brak wartości w config dla klucza: {key!r}")
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
) -> None:
    """Wpis do `events`. details serializowany do JSON jeśli niepusty.

    ts_utc zapisujemy explicite Python isoformat (z 'T' i timezone) — SQLite
    CURRENT_TIMESTAMP używa formatu ze spacją, nieporównywalnego lexicographically z naszymi cutoff.
    """
    payload = json.dumps(details, separators=(",", ":"), default=str) if details else None
    conn.execute(
        "INSERT INTO events(event_type, details_json, ts_utc) VALUES (?, ?, ?)",
        (event_type, payload, datetime.now(timezone.utc).isoformat()),
    )


def ensure_daily_state(conn: sqlite3.Connection, date_local: date) -> sqlite3.Row:
    """Zwraca rekord daily_state dla danej daty, tworząc pusty jeśli nie istnieje.

    Fast path: gdy rekord ISTNIEJE (typowa sytuacja w trakcie dnia), wykonujemy TYLKO
    SELECT — żaden writer lock nie jest brany. Bez tego każdy GET /admin/api/status
    otwierał implicit write transaction (przez INSERT OR IGNORE), co przy parallel
    requests (auto-refresh UI + watchdog heartbeat) prowadziło do "database is locked"
    pomimo busy_timeout=30s.

    Slow path: rekord nie istnieje (pierwszy request dnia po północy) → INSERT i znów SELECT.
    State that is not tied to a calendar day is carried over from the most recent previous row:
    - manual_lock: stays until the parent removes it,
    - night_extension_until_utc: a night window spans midnight, so a grant made before midnight
      must still apply after it (only carried while it is still in the future).
    """
    row = conn.execute(
        "SELECT * FROM daily_state WHERE date_local = ?",
        (date_local.isoformat(),),
    ).fetchone()
    if row is not None:
        return row
    # First request of this date — pisać.
    conn.execute(
        "INSERT INTO daily_state(date_local) VALUES (?) "
        "ON CONFLICT(date_local) DO NOTHING",
        (date_local.isoformat(),),
    )
    prev = conn.execute(
        "SELECT manual_lock, night_extension_until_utc FROM daily_state WHERE date_local < ? "
        "ORDER BY date_local DESC LIMIT 1",
        (date_local.isoformat(),),
    ).fetchone()
    if prev:
        carried: dict = {}
        if prev["manual_lock"]:
            carried["manual_lock"] = 1
        ext = prev["night_extension_until_utc"]
        if ext and datetime.fromisoformat(ext.replace("Z", "+00:00")) > datetime.now(timezone.utc):
            carried["night_extension_until_utc"] = ext
        update_daily_state(conn, date_local, **carried)
    return conn.execute(
        "SELECT * FROM daily_state WHERE date_local = ?",
        (date_local.isoformat(),),
    ).fetchone()


def update_daily_state(conn: sqlite3.Connection, date_local: date, **fields) -> None:
    """UPDATE z wybranych pól. Buduje SQL dynamicznie — wartości przez parametry, klucze
    pochodzą tylko ze zaufanego kodu, nie z user input.
    """
    if not fields:
        return
    set_clause = ", ".join(f"{k} = ?" for k in fields)
    values = [*fields.values(), date_local.isoformat()]
    conn.execute(
        f"UPDATE daily_state SET {set_clause} WHERE date_local = ?",
        values,
    )
