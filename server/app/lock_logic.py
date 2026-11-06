"""Wspólna logika wyznaczania lock_reason + helper funkcje datetime.

Wywoływana z heartbeat, totp i admin_api — jedna implementacja zamiast kopii.
Kolejność priorytetów (spec sekcja 6.3): manual > night > limit.

Helper funkcje (parse_hhmm, is_weekend, is_in_night_window, parse_iso_utc) są tu
zamiast w heartbeat.py żeby uniknąć circular import: heartbeat ↔ lock_logic.
"""

from __future__ import annotations

import sqlite3
from datetime import date, datetime, time, timezone
from zoneinfo import ZoneInfo

from .db import get_config_int, get_config_value


# === Datetime helpers ===


def utc_iso(dt: datetime) -> str:
    """ISO 8601 z 'Z' suffix (zgodnie ze przykładami w spec sekcji 5.2)."""
    return dt.astimezone(timezone.utc).isoformat(timespec="milliseconds").replace("+00:00", "Z")


def parse_iso_utc(s: str) -> datetime:
    """Parsuje ISO; 'Z' i naive interpretuje jako UTC."""
    if s.endswith("Z"):
        s = s[:-1] + "+00:00"
    dt = datetime.fromisoformat(s)
    if dt.tzinfo is None:
        dt = dt.replace(tzinfo=timezone.utc)
    return dt.astimezone(timezone.utc)


def parse_hhmm(s: str) -> time:
    """'21:30' -> time(21, 30)."""
    h, m = s.split(":")
    return time(int(h), int(m))


def is_weekend(d: date) -> bool:
    """Spec sekcja 4.1: piatek liczy sie jako weekend (Pia-Nd: 240 min).

    weekday(): Mon=0, Tue=1, Wed=2, Thu=3, Fri=4, Sat=5, Sun=6.
    """
    return d.weekday() >= 4


def is_in_night_window(now_local_time: time, night_start: time, night_end: time) -> bool:
    """True jesli aktualna pora lokalna jest w przedziale night_start..night_end.

    Obsluguje cross-midnight (typowy case: 21:30 -> 06:30).
    """
    if night_start < night_end:
        return night_start <= now_local_time < night_end
    return now_local_time >= night_start or now_local_time < night_end


# === Lock config loader ===


def load_lock_config(conn: sqlite3.Connection) -> dict:
    """Odczytuje z DB wszystkie klucze potrzebne do compute_lock_reason.

    Używane przez ścieżki nie-heartbeat (admin, totp, pause) które nie korzystają
    z cache. Heartbeat używa _get_hb_cfg() który batch-cachuje te i inne klucze.
    """
    keys = ("night_lock_start", "night_lock_end",
            "daily_limit_weekday", "daily_limit_weekend")
    rows = dict(
        conn.execute(
            "SELECT key, value FROM config WHERE key IN (?, ?, ?, ?)", keys
        ).fetchall()
    )
    return {
        "night_lock_start":    rows.get("night_lock_start", "22:00"),
        "night_lock_end":      rows.get("night_lock_end",   "07:00"),
        "daily_limit_weekday": int(rows.get("daily_limit_weekday", "120")),
        "daily_limit_weekend": int(rows.get("daily_limit_weekend", "240")),
    }


# === Lock determination ===


def compute_lock_reason(
    now_utc: datetime,
    tz: ZoneInfo,
    today_local: date,
    state: dict,
    cfg: dict,
) -> str | None:
    """Wyznacza aktualny lock_reason bez modyfikowania stanu DB.

    Zwraca: 'manual' | 'night' | 'limit' | 'pause' | None (brak blokady).

    cfg musi zawierać: night_lock_start, night_lock_end,
                       daily_limit_weekday, daily_limit_weekend.
    Można przekazać wynik load_lock_config(conn) lub pole z heartbeat cache.
    """
    if state["manual_lock"]:
        return "manual"

    night_start = parse_hhmm(cfg["night_lock_start"])
    night_end   = parse_hhmm(cfg["night_lock_end"])
    in_night    = is_in_night_window(now_utc.astimezone(tz).time(), night_start, night_end)

    night_extended = False
    if state["night_extension_until_utc"]:
        ext_until = parse_iso_utc(state["night_extension_until_utc"])
        if now_utc < ext_until:
            night_extended = True

    if in_night and not night_extended:
        return "night"

    minutes_used = state["seconds_used"] // 60
    daily_limit  = cfg["daily_limit_weekend"] if is_weekend(today_local) else cfg["daily_limit_weekday"]
    if minutes_used >= daily_limit + state["limit_override_minutes"]:
        return "limit"

    # Pauza — najniższy priorytet. Aktywna tylko gdy dziecko ma jeszcze czas.
    if state.get("pause_active"):
        return "pause"

    return None
