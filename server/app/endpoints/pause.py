"""POST /v1/pause_toggle — the child's pause, from the tray icon or "Jestem z powrotem".

Pause: lock_reason = "pause", no time accrues, screen locked. Per child (D7): a pause started on one
PC locks all of the child's PCs, like every other lock.
"""

from __future__ import annotations

import sqlite3
from datetime import datetime, timezone
from zoneinfo import ZoneInfo

from fastapi import APIRouter, Depends

from ..auth import Device, authenticate_request
from ..db import get_child, get_config_value, get_db, log_event, update_child_day
from ..lock_logic import child_today, compute_lock_reason

router = APIRouter()


@router.post("/v1/pause_toggle")
async def pause_toggle(
    device: Device = Depends(authenticate_request),
    db: sqlite3.Connection = Depends(get_db),
) -> dict:
    now_utc = datetime.now(timezone.utc)
    tz = ZoneInfo(get_config_value(db, "timezone"))
    today = child_today(db, get_child(db, device.child_id), now_utc, tz)
    state = today["state"]

    # A higher-priority lock (manual/night/limit) makes a pause pointless: would it lock without the pause?
    higher_lock = compute_lock_reason(now_utc, tz, today["today"], {**state, "pause_active": 0}, today["cfg"])
    if higher_lock is not None:
        return {"ok": False, "paused": bool(state["pause_active"]), "blocked_by": higher_lock}

    new_pause = 0 if state["pause_active"] else 1
    update_child_day(db, device.child_id, today["today"], pause_active=new_pause)
    log_event(db, "pause_toggled", {"pause_active": bool(new_pause)},
              child_id=device.child_id, device_id=device.id)
    return {"ok": True, "paused": bool(new_pause)}
