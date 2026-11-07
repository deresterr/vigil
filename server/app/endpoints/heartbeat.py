"""POST /v1/heartbeat — pełna logika tickowania z sekcji 6.3 spec'a.

Reaktywne liczenie czasu: serwer NIE ma ticka, każde zapytanie watchdoga przesuwa licznik
o `delta` (clamped 0..15s) jeśli ostatnia aktywność była niedawno (gap detection).
"""

from __future__ import annotations

import sqlite3
from datetime import datetime, timedelta, timezone
from typing import Any
from zoneinfo import ZoneInfo

from fastapi import APIRouter, Depends, HTTPException, Request

from ..auth import authenticate_request
from ..db import (
    ensure_daily_state,
    get_config_value,
    get_config_values,
    get_db,
    log_event,
    set_config_value,
    update_daily_state,
)
from ..lock_logic import (
    compute_lock_reason,
    is_weekend,
    load_lock_config,
    parse_iso_utc,
    utc_iso,
)
from ..offline import sync_offline_totp
from ..tamper import GOODBYE_REASONS, process_goodbye, process_telemetry

router = APIRouter()


def _load_hb_cfg(db: sqlite3.Connection) -> dict:
    """Config for one heartbeat, read fresh every time (one SELECT; no cache to invalidate)."""
    cfg = load_lock_config(db)
    extra = get_config_values(db, ("timezone", "heartbeat_delta_max", "gap_detection_threshold",
                                   "totp_secret", "events_retention_days"))
    cfg.update(
        timezone=extra["timezone"],
        heartbeat_delta_max=int(extra["heartbeat_delta_max"]),
        gap_detection_threshold=int(extra["gap_detection_threshold"]),
        totp_secret=extra["totp_secret"],
        events_retention_days=int(extra["events_retention_days"]),
    )
    return cfg


# Counter dla okresowego cleanup events (single-worker uvicorn → bezpieczne).
_heartbeat_counter = 0

# Polskie komunikaty per lock_reason.
# `server_unreachable` ustawia watchdog lokalnie — tu nie pojawia się.
_UI_MESSAGES_TEMPLATE: dict[str, str] = {
    "limit":  "Skończył się Twój czas na dziś. Zapraszam jutro! :)",
    "night":  "Pora spać. Komputer odblokuje się rano.",
    "manual": "Komputer został zablokowany przez rodzica.",
    "pause":  "Pauza — czas nie leci. Wróć gdy będziesz gotowy!",
}


def _validate_request_body(body: Any) -> dict:
    if not isinstance(body, dict):
        raise HTTPException(status_code=422, detail="body_must_be_object")

    required_types: dict[str, type] = {
        "session_active": bool,
        "user_active": bool,
        "current_time_utc": str,
        "uptime_seconds": int,
    }
    for key, expected_type in required_types.items():
        if key not in body:
            raise HTTPException(status_code=422, detail=f"missing_field: {key}")
        # bool jest podtypem int, więc sprawdzamy bool najpierw
        if expected_type is bool and not isinstance(body[key], bool):
            raise HTTPException(status_code=422, detail=f"invalid_type: {key}")
        if expected_type is int and (isinstance(body[key], bool) or not isinstance(body[key], int)):
            raise HTTPException(status_code=422, detail=f"invalid_type: {key}")
        if expected_type is str and not isinstance(body[key], str):
            raise HTTPException(status_code=422, detail=f"invalid_type: {key}")

    # Tamper telemetry (#7). Optional so a watchdog without it still works; all or nothing.
    telemetry = ("instance_id", "awake_ms", "awake_ms_at_start", "failed_attempts")
    present = [k for k in telemetry if k in body]
    if present and len(present) != len(telemetry):
        raise HTTPException(status_code=422, detail="incomplete_telemetry")
    if present:
        if not isinstance(body["instance_id"], str) or not 0 < len(body["instance_id"]) <= 64:
            raise HTTPException(status_code=422, detail="invalid_type: instance_id")
        for k in telemetry[1:]:
            v = body[k]
            if isinstance(v, bool) or not isinstance(v, int) or v < 0:
                raise HTTPException(status_code=422, detail=f"invalid_type: {k}")

    # Offline TOTP (#9). totp_fp: which secret the watchdog holds; offline_grants: unlocks it did
    # on its own while the server was unreachable, reported on reconnect.
    if "totp_fp" in body and (not isinstance(body["totp_fp"], str) or len(body["totp_fp"]) > 64):
        raise HTTPException(status_code=422, detail="invalid_type: totp_fp")
    grants = body.get("offline_grants", [])
    if not isinstance(grants, list) or len(grants) > 50:
        raise HTTPException(status_code=422, detail="invalid_type: offline_grants")
    for g in grants:
        ok = (isinstance(g, dict) and isinstance(g.get("at_utc"), str)
              and all(isinstance(g.get(k), int) and not isinstance(g.get(k), bool) for k in ("minutes", "step")))
        if not ok:
            raise HTTPException(status_code=422, detail="invalid_type: offline_grants")

    return body


# === Cleanup ===


def _maybe_cleanup_events(conn: sqlite3.Connection, cfg: dict) -> None:
    """Co 100 heartbeatów usuń events starsze niż retention (sekcja 6.6)."""
    global _heartbeat_counter
    _heartbeat_counter += 1
    if _heartbeat_counter % 100 != 0:
        return
    retention = cfg["events_retention_days"]
    cutoff = datetime.now(timezone.utc) - timedelta(days=retention)
    conn.execute("DELETE FROM events WHERE ts_utc < ?", (cutoff.isoformat(),))


# === Endpoint ===


@router.post("/v1/heartbeat")
async def heartbeat(
    request: Request,
    _: None = Depends(authenticate_request),
    db: sqlite3.Connection = Depends(get_db),
):
    raw_body = await request.json()
    body = _validate_request_body(raw_body)

    # === 0. Batch-load config (cache 60s, jeden SELECT dla wszystkich kluczy) ===
    cfg = _load_hb_cfg(db)

    now_utc = datetime.now(timezone.utc)
    tz = ZoneInfo(cfg["timezone"])
    now_local = now_utc.astimezone(tz)
    today_local = now_local.date()

    state = dict(ensure_daily_state(db, today_local))

    # === 1. Tickowanie (sekcja 6.3) ===
    seconds_added = 0
    if state["last_heartbeat_utc"] and body["session_active"] and body["user_active"]:
        last_hb = parse_iso_utc(state["last_heartbeat_utc"])
        delta_seconds = (now_utc - last_hb).total_seconds()
        delta_seconds = max(0.0, min(float(cfg["heartbeat_delta_max"]), delta_seconds))
        if state["last_session_active_utc"]:
            last_active = parse_iso_utc(state["last_session_active_utc"])
            if (now_utc - last_active).total_seconds() <= cfg["gap_detection_threshold"]:
                seconds_added = int(delta_seconds)

    # Pre-lock check: jeśli już zablokowany — czas nie leci.
    # Optymalizacja: compute_lock_reason wywołane RAZ z aktualnym stanem.
    # Jeśli seconds_added > 0, wywołamy drugi raz z nowym seconds_used żeby sprawdzić
    # czy właśnie przekroczyliśmy limit. Jeśli seconds_added == 0, obie odpowiedzi są identyczne.
    pre_lock = compute_lock_reason(now_utc, tz, today_local, state, cfg)
    if pre_lock is not None:
        seconds_added = 0

    new_seconds_used = state["seconds_used"] + seconds_added

    # === 2. Update state ===
    update_fields: dict = {"last_heartbeat_utc": now_utc.isoformat()}
    if seconds_added > 0:
        update_fields["seconds_used"] = new_seconds_used  # tylko gdy się zmieniło
    if body["session_active"] and body["user_active"]:
        update_fields["last_session_active_utc"] = now_utc.isoformat()
    update_daily_state(db, today_local, **update_fields)

    # === 3. Lock determination ===
    # Jeśli seconds_added == 0: stan się nie zmienił → lock_reason == pre_lock (0 dodatkowych SELECT-ów)
    # Jeśli seconds_added > 0: nowe seconds_used mogły trafić w limit → sprawdź ponownie
    if seconds_added > 0:
        lock_reason = compute_lock_reason(
            now_utc, tz, today_local,
            {**state, "seconds_used": new_seconds_used},
            cfg,
        )
    else:
        lock_reason = pre_lock  # identyczny stan — ten sam wynik, zero duplikatów SELECT

    # Journal (#8): the moment a limit/night lock starts. Manual lock and pause are logged by their
    # own endpoints; unlocks by the grant that caused them.
    last_reason = get_config_value(db, "last_lock_reason") or ""
    if (lock_reason or "") != last_reason:
        if lock_reason in ("limit", "night"):
            log_event(db, "lock_started", {"reason": lock_reason, "minutes_used": new_seconds_used // 60})
        set_config_value(db, "last_lock_reason", lock_reason or "")

    minutes_used = new_seconds_used // 60
    daily_limit  = cfg["daily_limit_weekend"] if is_weekend(today_local) else cfg["daily_limit_weekday"]
    effective_limit = daily_limit + state["limit_override_minutes"]
    minutes_remaining = max(0, effective_limit - minutes_used)

    # === 4. Clock drift (informacyjny, nie blokuje) ===
    try:
        client_now = parse_iso_utc(body["current_time_utc"])
        drift = abs((client_now - now_utc).total_seconds())
        if drift > 180:
            log_event(db, "clock_drift", {"drift_seconds": int(drift)})
    except (ValueError, KeyError):
        pass

    # === 5. UI state (tylko gdy zablokowany) ===
    if lock_reason:
        ui_state = {
            "main_message": _UI_MESSAGES_TEMPLATE.get(lock_reason),
            "show_totp_field": bool(cfg["totp_secret"]) and lock_reason in ("limit", "night", "manual"),
            "show_resume_button": lock_reason == "pause",
            "show_retry_button": False,
            "show_shutdown_button": True,
            "current_time_display": now_local.strftime("%H:%M"),
        }
    else:
        ui_state = None

    # === 6. Tamper telemetry (#7) ===
    if "instance_id" in body:
        process_telemetry(db, body["instance_id"], body["awake_ms"], body["awake_ms_at_start"],
                          body["failed_attempts"])

    # === 7. Offline TOTP (#9) ===
    offline_secret = sync_offline_totp(db, body.get("totp_fp"), body.get("offline_grants", []))

    # === 8. Cleanup events (co 100 hb) ===
    _maybe_cleanup_events(db, cfg)

    return {
        "lock_required": lock_reason is not None,
        "lock_reason": lock_reason,
        "minutes_used": minutes_used,
        "minutes_remaining": minutes_remaining,
        "next_check_seconds": 5 if body["session_active"] else 30,
        "server_time_utc": utc_iso(now_utc),
        "ui_state": ui_state,
        # Only when the watchdog holds a different (or no) TOTP secret; the response is HMAC-signed.
        **({"totp_secret": offline_secret} if offline_secret else {}),
    }


@router.post("/v1/goodbye")
async def goodbye(
    request: Request,
    _: None = Depends(authenticate_request),
    db: sqlite3.Connection = Depends(get_db),
):
    """Sent by the watchdog when its service stops (system shutdown or service stop)."""
    body = await request.json()
    if not isinstance(body, dict):
        raise HTTPException(status_code=422, detail="body_must_be_object")
    instance_id, reason = body.get("instance_id"), body.get("reason")
    if not isinstance(instance_id, str) or not 0 < len(instance_id) <= 64:
        raise HTTPException(status_code=422, detail="invalid_type: instance_id")
    if reason not in GOODBYE_REASONS:
        raise HTTPException(status_code=422, detail="invalid_reason")
    process_goodbye(db, instance_id, reason)
    return {"ok": True}


# Tamper events the watchdog observes itself (#10). Wire type -> journal event type.
_TAMPER_EVENTS = {"lockscreen_killed": "tamper_lockscreen_killed"}


@router.post("/v1/tamper_event")
async def tamper_event(
    request: Request,
    _: None = Depends(authenticate_request),
    db: sqlite3.Connection = Depends(get_db),
):
    body = await request.json()
    if not isinstance(body, dict) or body.get("type") not in _TAMPER_EVENTS:
        raise HTTPException(status_code=422, detail="invalid_type")
    details = {k: body[k] for k in ("kills", "window_seconds", "logged_off")
               if isinstance(body.get(k), int | bool)}
    log_event(db, _TAMPER_EVENTS[body["type"]], details)
    return {"ok": True}
