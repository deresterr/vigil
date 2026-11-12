"""POST /v1/pause_toggle — the child's pause, from the tray icon (toggle) or "I'm back" ({"paused": false}).

Pause: lock_reason = "pause", no time accrues, screen locked. Per child (D7): a pause started on one
PC locks all of the child's PCs, like every other lock. A target state makes a repeated "I'm back" (a double
click, a click on each monitor) harmless; without one it toggles (the tray icon).
"""

from __future__ import annotations

import sqlite3
from datetime import datetime, timezone
from zoneinfo import ZoneInfo

from fastapi import APIRouter, Depends

from ..auth import Device, authenticate_request
from ..db import get_child, get_config_value, get_db, log_event, update_child_day
from ..lock_logic import child_today, compute_lock_reason
from ..models import PauseRequest

router = APIRouter()


@router.post("/v1/pause_toggle")
async def pause_toggle(
    body: PauseRequest | None = None,
    device: Device = Depends(authenticate_request),
    db: sqlite3.Connection = Depends(get_db),
) -> dict:
    now_utc = datetime.now(timezone.utc)
    tz = ZoneInfo(get_config_value(db, "timezone"))
    today = child_today(db, get_child(db, device.child_id), now_utc, tz)
    state = today["state"]
    paused = bool(state["pause_active"])
    wanted = not paused if body is None or body.paused is None else body.paused
    if wanted == paused:
        return {"ok": True, "paused": paused}

    if wanted:
        # A higher-priority lock (manual/night/limit) makes a pause pointless: would it lock without the pause?
        higher_lock = compute_lock_reason(now_utc, tz, today["today"], {**state, "pause_active": 0}, today["cfg"])
        if higher_lock is not None:
            return {"ok": False, "paused": paused, "blocked_by": higher_lock}

    update_child_day(db, device.child_id, today["today"], pause_active=int(wanted))
    log_event(db, "pause_toggled", {"pause_active": wanted}, child_id=device.child_id, device_id=device.id)
    return {"ok": True, "paused": wanted}
