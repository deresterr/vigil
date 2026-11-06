"""Journal tab of the parent panel (#8): what happened on the child's PC, plus alerts to confirm.

The server prepares data (which events, pause start/end paired into one entry, PC session length,
per-day totals); admin.html owns the Polish wording and the category filters.
"""

from __future__ import annotations

import json
import sqlite3
from datetime import date, datetime, time, timedelta, timezone
from zoneinfo import ZoneInfo

from fastapi import APIRouter, Depends, HTTPException, Request

from ..admin_auth import require_admin_session
from ..db import get_config_value, get_db
from ..lock_logic import is_weekend

router = APIRouter(prefix="/admin/api", dependencies=[Depends(require_admin_session)])

# Alerts: tamper_* plus signs of guessing. Shown until the parent confirms them.
ALERT_EXTRA_TYPES = ("totp_lockout", "admin_login_locked", "pairing_code_invalidated")
ALERT_WHERE = (
    "(event_type LIKE 'tamper\\_%' ESCAPE '\\' OR event_type IN ("
    + ",".join(f"'{t}'" for t in ALERT_EXTRA_TYPES)
    + "))"
)

# Everything else the journal shows (decision: parent actions only when they change locking).
JOURNAL_TYPES = (
    "watchdog_started", "watchdog_shutdown", "watchdog_offline",
    "lock_started", "pause_toggled",
    "manual_lock_set", "manual_lock_cleared", "manual_unlock", "grant_applied", "extensions_cleared",
    "offline_unlock",
)

MAX_DAYS = 14


def _is_alert(event_type: str) -> bool:
    return event_type.startswith("tamper_") or event_type in ALERT_EXTRA_TYPES


def _day_bounds_utc(day: date, tz: ZoneInfo) -> tuple[str, str]:
    start = datetime.combine(day, time.min, tzinfo=tz)
    end = datetime.combine(day + timedelta(days=1), time.min, tzinfo=tz)
    return start.astimezone(timezone.utc).isoformat(), end.astimezone(timezone.utc).isoformat()


def _entries(rows: list[sqlite3.Row], tz: ZoneInfo) -> list[dict]:
    """Rows oldest-first in, entries newest-first out. Pairs pause on/off; adds PC session length."""
    out: list[dict] = []
    pause_open: dict | None = None
    pc_on_since: datetime | None = None
    for r in rows:
        ts = datetime.fromisoformat(r["ts_utc"]).astimezone(tz)
        details = json.loads(r["details_json"]) if r["details_json"] else {}
        etype = r["event_type"]
        entry = {"id": r["id"], "time": ts.strftime("%H:%M"), "type": etype, "details": details,
                 "alert": _is_alert(etype), "acked": r["acked_utc"] is not None}

        if etype == "pause_toggled":
            if details.get("pause_active"):
                pause_open = {**entry, "type": "pause", "details": {"from": entry["time"], "to": None}}
                pause_open["_start"] = ts
                out.append(pause_open)
            elif pause_open is not None:
                pause_open["details"]["to"] = entry["time"]
                pause_open["details"]["minutes"] = int((ts - pause_open.pop("_start")).total_seconds() // 60)
                pause_open = None
            continue

        if etype == "watchdog_started" and not details.get("same_boot"):
            pc_on_since = ts
        elif etype == "watchdog_shutdown" and pc_on_since is not None:
            details = {**details, "on_since": pc_on_since.strftime("%H:%M"),
                       "minutes": int((ts - pc_on_since).total_seconds() // 60)}
            entry["details"] = details
            pc_on_since = None
        out.append(entry)

    for e in out:
        e.pop("_start", None)
    return list(reversed(out))


@router.get("/journal")
async def journal(db: sqlite3.Connection = Depends(get_db), before: str | None = None, days: int = 3) -> dict:
    """Days newest first. `before` (YYYY-MM-DD, exclusive) pages to older days."""
    if not 1 <= days <= MAX_DAYS:
        raise HTTPException(status_code=422, detail="days_out_of_range")
    tz = ZoneInfo(get_config_value(db, "timezone"))
    today = datetime.now(timezone.utc).astimezone(tz).date()
    try:
        first = date.fromisoformat(before) - timedelta(days=1) if before else today
    except ValueError as e:
        raise HTTPException(status_code=422, detail="invalid_before") from e

    types = ",".join(f"'{t}'" for t in JOURNAL_TYPES)
    limit_wd = int(get_config_value(db, "daily_limit_weekday"))
    limit_we = int(get_config_value(db, "daily_limit_weekend"))
    out = []
    for i in range(days):
        day = first - timedelta(days=i)
        lo, hi = _day_bounds_utc(day, tz)
        rows = db.execute(
            f"SELECT id, ts_utc, event_type, details_json, acked_utc FROM events "
            f"WHERE ts_utc >= ? AND ts_utc < ? AND (event_type IN ({types}) OR {ALERT_WHERE}) ORDER BY id",
            (lo, hi),
        ).fetchall()
        state = db.execute(
            "SELECT seconds_used, limit_override_minutes FROM daily_state WHERE date_local = ?", (day.isoformat(),)
        ).fetchone()
        base = limit_we if is_weekend(day) else limit_wd
        out.append({
            "date": day.isoformat(),
            "minutes_used": (state["seconds_used"] // 60) if state else 0,
            "limit_minutes": base + (state["limit_override_minutes"] if state else 0),
            "entries": _entries(rows, tz),
        })
    oldest = db.execute("SELECT MIN(ts_utc) FROM events").fetchone()[0]
    has_more = bool(oldest) and _day_bounds_utc(first - timedelta(days=days - 1), tz)[0] > oldest
    return {"days": out, "has_more": has_more}


@router.post("/alerts/ack")
async def ack_alerts(request: Request, db: sqlite3.Connection = Depends(get_db)) -> dict:
    """Confirm alerts: body {"ids": [...]} for specific ones, {} for all."""
    body = await request.json()
    now = datetime.now(timezone.utc).isoformat()
    ids = body.get("ids") if isinstance(body, dict) else None
    if ids is None:
        cur = db.execute(f"UPDATE events SET acked_utc = ? WHERE {ALERT_WHERE} AND acked_utc IS NULL", (now,))
    else:
        if not isinstance(ids, list) or not all(isinstance(i, int) and not isinstance(i, bool) for i in ids):
            raise HTTPException(status_code=422, detail="ids_must_be_int_list")
        marks = ",".join("?" * len(ids)) or "NULL"
        cur = db.execute(
            f"UPDATE events SET acked_utc = ? WHERE id IN ({marks}) AND {ALERT_WHERE} AND acked_utc IS NULL",
            (now, *ids),
        )
    return {"ok": True, "acked": cur.rowcount}
