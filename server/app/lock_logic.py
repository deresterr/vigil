"""The lock reason and its datetime helpers: one implementation for heartbeat, totp and admin_api.

Priority: manual > night > limit > pause.

Also the weekly schedule (#48): per-day limit and the night window that belongs to each evening.
Helpers live here rather than in heartbeat.py to avoid a heartbeat <-> lock_logic import cycle.
"""

from __future__ import annotations

import sqlite3
from datetime import date, datetime, time, timedelta, timezone
from zoneinfo import ZoneInfo

from .config import default_schedule
from .db import ensure_child_day

# === Datetime helpers ===


def utc_iso(dt: datetime) -> str:
    """ISO 8601 UTC with a 'Z' suffix, milliseconds."""
    return dt.astimezone(timezone.utc).isoformat(timespec="milliseconds").replace("+00:00", "Z")


def parse_iso_utc(s: str) -> datetime:
    """Parses ISO 8601; 'Z' and a naive time are UTC."""
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


# === Weekly schedule (#48) ===

# A night_start before noon is after midnight: "Friday 00:30-09:00" is the night from Friday to Saturday.
_AFTER_MIDNIGHT_BEFORE = time(12, 0)


def load_lock_config(conn: sqlite3.Connection, child_id: int) -> dict:
    """The child's week for compute_lock_reason: cfg["days"][weekday] (Monday = 0) holds daily_limit,
    night_start, night_end. Read per request, no cache."""
    days = default_schedule()  # only a child without schedule rows (should not exist) falls back to it
    for row in conn.execute("SELECT weekday, daily_limit, night_start, night_end FROM child_schedule "
                            "WHERE child_id = ?", (child_id,)):
        days[row["weekday"]] = {"daily_limit": int(row["daily_limit"]), "night_start": row["night_start"],
                                "night_end": row["night_end"]}
    return {"days": days}


# daily_limit 0 = no limit that day (#106): no limit lock, extra time refused (admin_api.grant), the PC gets
# minutes_remaining 0 and shows no countdown. A day without the PC is the manual lock.
NO_LIMIT = 0


def daily_limit(cfg: dict, day: date) -> int:
    return cfg["days"][day.weekday()]["daily_limit"]


def minutes_remaining(limit: int, override: int, used: int) -> int:
    """Minutes left today; 0 also on a day without a limit."""
    return 0 if limit == NO_LIMIT else max(0, limit + override - used)


def night_window(cfg: dict, day: date) -> tuple[datetime, datetime]:
    """The night belonging to the evening of `day` (#48), as naive local datetimes [start, end).

    It starts at that day's night_start, or on the next day when night_start is before noon (after
    midnight), and ends at the first night_end after its start: "Fri 23:00-09:00" = Fri 23:00 to Sat 09:00.
    """
    entry = cfg["days"][day.weekday()]
    start_t, end_t = parse_hhmm(entry["night_start"]), parse_hhmm(entry["night_end"])
    start_day = day if start_t >= _AFTER_MIDNIGHT_BEFORE else day + timedelta(days=1)
    end_day = start_day if end_t > start_t else start_day + timedelta(days=1)
    return datetime.combine(start_day, start_t), datetime.combine(end_day, end_t)


def _nearby_nights(cfg: dict, now_local: datetime) -> list[tuple[datetime, datetime]]:
    # A night starts on its day or the next one and lasts under 24 h, so only these can contain or
    # follow `now`: from the night of two days ago to tomorrow's.
    d = now_local.date()
    return [night_window(cfg, d + timedelta(days=i)) for i in range(-2, 2)]


def night_window_at(cfg: dict, now_local: datetime) -> tuple[datetime, datetime] | None:
    """The night window containing now_local (naive local time), or None."""
    now_local = now_local.replace(tzinfo=None)
    return next(((s, e) for s, e in _nearby_nights(cfg, now_local) if s <= now_local < e), None)


def current_or_next_night_start(cfg: dict, now_local: datetime) -> datetime:
    """Start of the night in progress, or of the next one (naive local time)."""
    now_local = now_local.replace(tzinfo=None)
    return next(s for s, e in sorted(_nearby_nights(cfg, now_local)) if e > now_local)


def child_today(conn: sqlite3.Connection, child: sqlite3.Row, now_utc: datetime, tz: ZoneInfo) -> dict:
    """Today's state of one child: its child_daily row, lock reason and minutes (all its devices)."""
    today = now_utc.astimezone(tz).date()
    state = dict(ensure_child_day(conn, child["id"], today))
    cfg = load_lock_config(conn, child["id"])
    limit = daily_limit(cfg, today)
    effective = limit + state["limit_override_minutes"]
    used = state["seconds_used"] // 60
    return {
        "today": today,
        "state": state,
        "cfg": cfg,
        "lock_reason": compute_lock_reason(now_utc, tz, today, state, cfg),
        "minutes_used": used,
        "daily_limit": limit,
        "unlimited": limit == NO_LIMIT,
        "effective_limit": effective,
        "minutes_remaining": minutes_remaining(limit, state["limit_override_minutes"], used),
    }


# === Lock determination ===


def compute_lock_reason(
    now_utc: datetime,
    tz: ZoneInfo,
    today_local: date,
    state: dict,
    cfg: dict,
) -> str | None:
    """The current lock reason, without changing the database.

    Returns 'manual' | 'night' | 'limit' | 'pause' | None (not locked).

    cfg: load_lock_config(conn, child_id).
    """
    if state["manual_lock"]:
        return "manual"

    in_night = night_window_at(cfg, now_utc.astimezone(tz)) is not None

    night_extended = False
    if state["night_extension_until_utc"]:
        ext_until = parse_iso_utc(state["night_extension_until_utc"])
        if now_utc < ext_until:
            night_extended = True

    if in_night and not night_extended:
        return "night"

    limit = daily_limit(cfg, today_local)
    if limit != NO_LIMIT and state["seconds_used"] // 60 >= limit + state["limit_override_minutes"]:
        return "limit"

    # Pause: the lowest priority, only while the child still has time left.
    if state.get("pause_active"):
        return "pause"

    return None
