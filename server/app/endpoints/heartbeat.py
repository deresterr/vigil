"""POST /v1/heartbeat (+ /v1/goodbye, /v1/tamper_event) — time accounting and lock decision.

Reactive: the server has no clock of its own; every heartbeat of a device moves its child's counter
by `delta` (clamped 0..heartbeat_delta_max) when the device was active recently (gap detection).
Per device (D7): delta and gap come from that device's own last heartbeat; the seconds go to its
child, so two PCs used at once count twice (decision: time = sum over devices). Locks are per child.
"""

from __future__ import annotations

import sqlite3
from datetime import datetime, timedelta, timezone
from zoneinfo import ZoneInfo

from fastapi import APIRouter, Depends, HTTPException

from ..auth import Device, authenticate_request
from ..db import get_child, get_config_values, get_db, log_event
from ..lock_logic import child_today, compute_lock_reason, parse_iso_utc, utc_iso
from ..models import GoodbyeRequest, GuardianRepaired, HeartbeatRequest, TamperEvent
from ..offline import sync_offline_totp
from ..tamper import process_goodbye, process_telemetry

router = APIRouter()


def _load_hb_cfg(db: sqlite3.Connection) -> dict:
    """Server-wide settings for one heartbeat, read fresh every time (one SELECT; no cache)."""
    v = get_config_values(db, ("timezone", "heartbeat_delta_max", "gap_detection_threshold",
                               "totp_secret", "events_retention_days"))
    return {
        "timezone": v["timezone"],
        "heartbeat_delta_max": int(v["heartbeat_delta_max"]),
        "gap_detection_threshold": int(v["gap_detection_threshold"]),
        "totp_secret": v["totp_secret"],
        "events_retention_days": int(v["events_retention_days"]),
    }


_heartbeat_counter = 0

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
    body: HeartbeatRequest,
    device: Device = Depends(authenticate_request),
    db: sqlite3.Connection = Depends(get_db),
):
    cfg = _load_hb_cfg(db)
    now_utc = datetime.now(timezone.utc)
    tz = ZoneInfo(cfg["timezone"])
    now_local = now_utc.astimezone(tz)
    child = get_child(db, device.child_id)
    ids = {"child_id": device.child_id, "device_id": device.id}
    dev = db.execute("SELECT last_heartbeat_utc, last_active_utc FROM devices WHERE id = ?",
                     (device.id,)).fetchone()

    # Pre-lock check: while the child is locked no time accrues (invariant).
    today = child_today(db, child, now_utc, tz)
    state, lock_cfg, today_local = today["state"], today["cfg"], today["today"]
    active = body.session_active and body.user_active

    # === 1. Time from this device ===
    seconds_added = 0
    if today["lock_reason"] is None and active and dev["last_heartbeat_utc"] and dev["last_active_utc"]:
        delta = (now_utc - parse_iso_utc(dev["last_heartbeat_utc"])).total_seconds()
        delta = max(0.0, min(float(cfg["heartbeat_delta_max"]), delta))
        if (now_utc - parse_iso_utc(dev["last_active_utc"])).total_seconds() <= cfg["gap_detection_threshold"]:
            seconds_added = int(delta)

    # === 2. Update device and child ===
    db.execute(
        "UPDATE devices SET last_heartbeat_utc = ?, last_active_utc = COALESCE(?, last_active_utc), "
        "seconds_today = CASE WHEN seconds_date = ? THEN seconds_today + ? ELSE ? END, seconds_date = ? "
        "WHERE id = ?",
        (now_utc.isoformat(), now_utc.isoformat() if active else None, today_local.isoformat(), seconds_added,
         seconds_added, today_local.isoformat(), device.id),
    )
    new_seconds_used = state["seconds_used"] + seconds_added
    if seconds_added:
        # Atomic: another device of the same child may have added its seconds meanwhile.
        db.execute("UPDATE child_daily SET seconds_used = seconds_used + ? WHERE child_id = ? AND date_local = ?",
                   (seconds_added, device.child_id, today_local.isoformat()))

    # === 3. Lock decision (again only if the new seconds may have reached the limit) ===
    lock_reason = today["lock_reason"]
    if seconds_added:
        lock_reason = compute_lock_reason(now_utc, tz, today_local, {**state, "seconds_used": new_seconds_used},
                                          lock_cfg)

    # Journal (#8): the moment a limit/night lock starts, once per child (not once per device).
    # Manual lock and pause are logged by their own endpoints; unlocks by the grant that caused them.
    if (lock_reason or "") != child["last_lock_reason"]:
        if lock_reason in ("limit", "night"):
            log_event(db, "lock_started", {"reason": lock_reason, "minutes_used": new_seconds_used // 60},
                      child_id=device.child_id)
        db.execute("UPDATE children SET last_lock_reason = ? WHERE id = ?", (lock_reason or "", device.child_id))

    minutes_used = new_seconds_used // 60
    minutes_remaining = max(0, today["effective_limit"] - minutes_used)

    # === 4. Clock drift (informational) ===
    try:
        drift = abs((parse_iso_utc(body.current_time_utc) - now_utc).total_seconds())
        if drift > 180:
            log_event(db, "clock_drift", {"drift_seconds": int(drift)}, **ids)
    except ValueError:
        pass

    # === 5. UI state (only while locked) ===
    # No sentences: vigil_ui words the lock screen in the child's Windows language from lock_reason (i18n, #52).
    ui_state = None
    if lock_reason:
        ui_state = {
            "show_totp_field": bool(cfg["totp_secret"]) and lock_reason in ("limit", "night", "manual"),
            "show_resume_button": lock_reason == "pause",
            "show_retry_button": False,
            "show_shutdown_button": True,
            "current_time_display": now_local.strftime("%H:%M"),
        }

    # === 6. Tamper telemetry (#7) ===
    if body.has_telemetry:
        process_telemetry(db, device, body.instance_id, body.awake_ms, body.awake_ms_at_start, body.failed_attempts)

    # === 7. Offline TOTP (#9) ===
    offline_secret = sync_offline_totp(db, device, body.totp_fp, [g.model_dump() for g in body.offline_grants])

    # === 8. Cleanup events (every 100 heartbeats) ===
    _maybe_cleanup_events(db, cfg)

    return {
        "lock_required": lock_reason is not None,
        "lock_reason": lock_reason,
        "minutes_used": minutes_used,
        "minutes_remaining": minutes_remaining,
        "next_check_seconds": 5 if body.session_active else 30,
        "server_time_utc": utc_iso(now_utc),
        "ui_state": ui_state,
        # Only when the watchdog holds a different (or no) TOTP secret; the response is HMAC-signed.
        **({"totp_secret": offline_secret} if offline_secret else {}),
    }


@router.post("/v1/goodbye")
async def goodbye(
    body: GoodbyeRequest,
    device: Device = Depends(authenticate_request),
    db: sqlite3.Connection = Depends(get_db),
):
    """Sent by the watchdog when its service stops (system shutdown or service stop)."""
    process_goodbye(db, device, body.instance_id, body.reason)
    return {"ok": True}


# The watchdog re-sends the guardian's events of the last day after every restart; older ones are noise.
_GUARDIAN_MAX_AGE = timedelta(days=2)


@router.post("/v1/tamper_event")
async def tamper_event(
    body: TamperEvent,
    device: Device = Depends(authenticate_request),
    db: sqlite3.Connection = Depends(get_db),
):
    """Tampering the watchdog observes itself: the lock UI killed in a loop (#10), guardian repairs (#36)."""
    ids = {"child_id": device.child_id, "device_id": device.id}
    if isinstance(body, GuardianRepaired):
        now = datetime.now(timezone.utc)
        occurred = body.occurred_utc.astimezone(timezone.utc)
        if not now - _GUARDIAN_MAX_AGE <= occurred <= now + timedelta(minutes=5):
            raise HTTPException(status_code=422, detail="invalid_guardian_report")
        occurred = min(occurred, now)
        # Event Log record IDs restart when the log is cleared: the time is part of the identity.
        seen = db.execute(
            "SELECT 1 FROM events WHERE event_type = 'tamper_guardian_repaired' AND device_id = ? "
            "AND json_extract(details_json, '$.record_id') = ? AND ts_utc = ?",
            (device.id, body.record_id, occurred.isoformat()),
        ).fetchone()
        if not seen:
            log_event(db, "tamper_guardian_repaired",
                      {"check": body.check, "repaired": body.repaired, "record_id": body.record_id},
                      ts_utc=occurred, **ids)
        return {"ok": True}

    log_event(db, "tamper_lockscreen_killed", body.model_dump(exclude={"type"}, exclude_none=True), **ids)
    return {"ok": True}
