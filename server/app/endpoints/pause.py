"""POST /v1/pause_toggle — przełącza pauzę inicjowaną przez dziecko.

Wywoływany przez watchdoga (ma bearer + HMAC) gdy dziecko kliknie ikonę w tray
lub przycisk "Jestem z powrotem" na ekranie blokady.
Pauza: lock_reason = "pause", czas nie leci, ekran zablokowany.
"""

from __future__ import annotations

import sqlite3
from datetime import datetime, timezone
from zoneinfo import ZoneInfo

from fastapi import APIRouter, Depends

from ..auth import authenticate_request
from ..db import ensure_daily_state, get_config_value, get_db, log_event, update_daily_state
from ..lock_logic import compute_lock_reason, load_lock_config

router = APIRouter()


@router.post("/v1/pause_toggle")
async def pause_toggle(
    _: None = Depends(authenticate_request),
    db: sqlite3.Connection = Depends(get_db),
) -> dict:
    now_utc = datetime.now(timezone.utc)
    tz = ZoneInfo(get_config_value(db, "timezone"))
    today_local = now_utc.astimezone(tz).date()

    state = dict(ensure_daily_state(db, today_local))

    # Blokuj toggle pauzy gdy aktywny wyższy priorytet (manual/night/limit).
    # compute_lock_reason traktuje pause_active=0 przy sprawdzaniu — interesuje nas czy
    # bez pauzy byłoby zablokowane z innego powodu. Tymczasowo zeruję pause_active w kopii.
    state_without_pause = {**state, "pause_active": 0}
    higher_lock = compute_lock_reason(now_utc, tz, today_local, state_without_pause, load_lock_config(db))
    if higher_lock is not None:
        # Wyższy priorytet blokady aktywny — pauza nie ma sensu, zwróć aktualny stan bez zmian.
        return {"ok": False, "paused": bool(state["pause_active"]), "blocked_by": higher_lock}

    new_pause = 0 if state["pause_active"] else 1
    update_daily_state(db, today_local, pause_active=new_pause)
    log_event(db, "pause_toggled", {"pause_active": bool(new_pause)})

    return {"ok": True, "paused": bool(new_pause)}
