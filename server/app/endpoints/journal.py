"""Journal tab of the parent panel (#8): what happened on the children's PCs, plus alerts to confirm.

The server prepares data (which events, pause start/end paired into one entry, PC session length,
per-day totals per child); admin.html owns the Polish wording and the category filters.
D7: every entry names its child and device (when there is one); `child_id` filters to one child.
"""

from __future__ import annotations

import json
import sqlite3
from datetime import date, datetime, time, timedelta, timezone
from zoneinfo import ZoneInfo

from fastapi import APIRouter, Depends, HTTPException, Request

from ..admin_auth import require_admin_session
from ..db import get_config_value, get_db
from ..lock_logic import daily_limit, load_lock_config

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
    "offline_unlock", "device_paired", "device_unpaired", "child_added", "child_removed",
)

MAX_DAYS = 14


def _is_alert(event_type: str) -> bool:
    return event_type.startswith("tamper_") or event_type in ALERT_EXTRA_TYPES


def _day_bounds_utc(day: date, tz: ZoneInfo) -> tuple[str, str]:
    start = datetime.combine(day, time.min, tzinfo=tz)
    end = datetime.combine(day + timedelta(days=1), time.min, tzinfo=tz)
    return start.astimezone(timezone.utc).isoformat(), end.astimezone(timezone.utc).isoformat()


def _entries(rows: list[sqlite3.Row], tz: ZoneInfo, names: dict) -> list[dict]:
    """Rows oldest-first in, entries newest-first out. Pairs pause on/off per child; adds PC session
    length per device."""
    out: list[dict] = []
    pause_open: dict[int | None, dict] = {}
    pc_on_since: dict[int | None, datetime] = {}
    for r in rows:
        ts = datetime.fromisoformat(r["ts_utc"]).astimezone(tz)
        details = json.loads(r["details_json"]) if r["details_json"] else {}
        etype = r["event_type"]
        child, device = r["child_id"], r["device_id"]
        entry = {"id": r["id"], "time": ts.strftime("%H:%M"), "type": etype, "details": details,
                 "alert": _is_alert(etype), "acked": r["acked_utc"] is not None,
                 # Removed children/devices keep their name in the event's details.
                 "child_id": child, "child_name": names["children"].get(child)
                 or (details.get("name") if etype == "child_removed" else None),
                 "device_id": device, "device_name": names["devices"].get(device)
                 or (details.get("name") if etype == "device_unpaired" else None)}

        if etype == "pause_toggled":
            if details.get("pause_active"):
                opened = {**entry, "type": "pause", "details": {"from": entry["time"], "to": None}, "_start": ts}
                pause_open[child] = opened
                out.append(opened)
            elif child in pause_open:
                opened = pause_open.pop(child)
                opened["details"]["to"] = entry["time"]
                opened["details"]["minutes"] = int((ts - opened.pop("_start")).total_seconds() // 60)
            continue

        if etype == "watchdog_started" and not details.get("same_boot"):
            pc_on_since[device] = ts
        elif etype == "watchdog_shutdown" and device in pc_on_since:
            since = pc_on_since.pop(device)
            entry["details"] = {**details, "on_since": since.strftime("%H:%M"),
                                "minutes": int((ts - since).total_seconds() // 60)}
        out.append(entry)

    for e in out:
        e.pop("_start", None)
    return list(reversed(out))


@router.get("/journal")
async def journal(db: sqlite3.Connection = Depends(get_db), before: str | None = None, days: int = 3,
                  child_id: int | None = None) -> dict:
    """Days newest first. `before` (YYYY-MM-DD, exclusive) pages to older days; `child_id` filters."""
    if not 1 <= days <= MAX_DAYS:
        raise HTTPException(status_code=422, detail="days_out_of_range")
    tz = ZoneInfo(get_config_value(db, "timezone"))
    today = datetime.now(timezone.utc).astimezone(tz).date()
    try:
        first = date.fromisoformat(before) - timedelta(days=1) if before else today
    except ValueError as e:
        raise HTTPException(status_code=422, detail="invalid_before") from e

    types = ",".join(f"'{t}'" for t in JOURNAL_TYPES)
    children = [c for c in db.execute("SELECT * FROM children ORDER BY id") if child_id in (None, c["id"])]
    names = {"children": {c["id"]: c["name"] for c in db.execute("SELECT id, name FROM children")},
             "devices": dict(db.execute("SELECT id, name FROM devices").fetchall())}
    child_filter, params = ("AND child_id = ?", (child_id,)) if child_id is not None else ("", ())
    out = []
    for i in range(days):
        day = first - timedelta(days=i)
        lo, hi = _day_bounds_utc(day, tz)
        rows = db.execute(
            f"SELECT id, ts_utc, event_type, details_json, acked_utc, child_id, device_id FROM events "
            f"WHERE ts_utc >= ? AND ts_utc < ? AND (event_type IN ({types}) OR {ALERT_WHERE}) {child_filter} "
            # By when it happened, not when the server heard of it: offline unlocks and outages
            # are reported on reconnect, minutes after the fact.
            f"ORDER BY ts_utc, id",
            (lo, hi, *params),
        ).fetchall()
        totals = []
        for c in children:
            state = db.execute("SELECT seconds_used, limit_override_minutes FROM child_daily "
                               "WHERE child_id = ? AND date_local = ?", (c["id"], day.isoformat())).fetchone()
            totals.append({
                "child_id": c["id"],
                "name": c["name"],
                "minutes_used": (state["seconds_used"] // 60) if state else 0,
                "limit_minutes": daily_limit(load_lock_config(c), day)
                + (state["limit_override_minutes"] if state else 0),
            })
        out.append({"date": day.isoformat(), "children": totals, "entries": _entries(rows, tz, names)})
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
