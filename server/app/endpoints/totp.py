"""POST /v1/totp_attempt — TOTP grant z auto-trybem (extend_limit/extend_night/manual_unlock)
i progresywnym lockoutem (sekcja 5.3, 5.4).

Server sam wybiera tryb na podstawie aktualnego lock_reason:
- lock=limit  → extend_limit (dodaje minutes_to_grant do limit_override)
- lock=night  → extend_night (przelicza per sekcja 4.5: until = night_start + minutes)
- lock=manual → manual_unlock (zdejmuje manual_lock; minutes ignorowane — interpretacja
                własna, spec milczy o tym przypadku)
- lock=None   → noop (TOTP nie ma sensu bez aktywnej blokady)
"""

from __future__ import annotations

import sqlite3
from datetime import datetime, timedelta, timezone
from zoneinfo import ZoneInfo

from fastapi import APIRouter, Depends
from fastapi.responses import JSONResponse

from ..auth import Device, authenticate_request
from ..crypto import match_totp_step
from ..db import get_child, get_config_value, get_db, log_event, set_config_value, update_child_day
from ..lock_logic import child_today, current_or_next_night_start, parse_iso_utc
from ..models import TotpAttemptRequest

router = APIRouter()

# Every 3rd consecutive failure starts a lockout. Capped at 15 min on purpose: a long lockout lets
# the child lock the parent out of unlocking. 3 tries per 15 min keeps guessing a 6-digit code
# (3 valid codes per window) at an expected ~2.5 years.
_FAILURES_PER_LOCKOUT = 3
_LOCKOUT_STEPS_SECONDS = (60, 300, 900)
_RESET_AFTER_SECONDS = 86400  # failure counter resets 24 h after the first failure


# === Lockout management ===
# One TOTP secret for all children (D7), so one lockout row (id = 1) for all of them.


def _check_lockout(conn: sqlite3.Connection, now_utc: datetime) -> int | None:
    """Zwraca pozostałe sekundy lockoutu lub None jeśli brak aktywnego lockoutu."""
    row = conn.execute(
        "SELECT lockout_until_utc FROM totp_lockout WHERE id = 1"
    ).fetchone()
    if not row or not row["lockout_until_utc"]:
        return None
    lockout_until = parse_iso_utc(row["lockout_until_utc"])
    if now_utc < lockout_until:
        return int((lockout_until - now_utc).total_seconds())
    return None


def _record_failure(conn: sqlite3.Connection, now_utc: datetime) -> tuple[int, int]:
    """Zwiększa licznik nieudanych prób, ustawia lockout jeśli próg osiągnięty.

    Returns: (new_attempts_count, lockout_seconds_remaining)
    """
    row = conn.execute(
        "SELECT attempts_count, first_attempt_utc FROM totp_lockout WHERE id = 1"
    ).fetchone()

    if not row or not row["first_attempt_utc"]:
        new_count = 1
        new_first = now_utc
    else:
        first_attempt = parse_iso_utc(row["first_attempt_utc"])
        # Reset licznika po 24h ciszy (sekcja 5.4)
        if (now_utc - first_attempt).total_seconds() > _RESET_AFTER_SECONDS:
            new_count = 1
            new_first = now_utc
        else:
            new_count = row["attempts_count"] + 1
            new_first = first_attempt

    new_lockout_until: datetime | None = None
    if new_count % _FAILURES_PER_LOCKOUT == 0:
        idx = min(new_count // _FAILURES_PER_LOCKOUT, len(_LOCKOUT_STEPS_SECONDS)) - 1
        new_lockout_until = now_utc + timedelta(seconds=_LOCKOUT_STEPS_SECONDS[idx])

    conn.execute(
        """INSERT INTO totp_lockout(id, attempts_count, first_attempt_utc, lockout_until_utc)
           VALUES (1, ?, ?, ?)
           ON CONFLICT(id) DO UPDATE SET
               attempts_count    = excluded.attempts_count,
               first_attempt_utc = excluded.first_attempt_utc,
               lockout_until_utc = excluded.lockout_until_utc""",
        (
            new_count,
            new_first.isoformat(),
            new_lockout_until.isoformat() if new_lockout_until else None,
        ),
    )

    seconds_remaining = (
        int((new_lockout_until - now_utc).total_seconds()) if new_lockout_until else 0
    )
    return new_count, seconds_remaining


def _reset_lockout(conn: sqlite3.Connection) -> None:
    """Po pierwszej udanej próbie kasujemy rekord (sekcja 5.4)."""
    conn.execute("DELETE FROM totp_lockout WHERE id = 1")


# === Helpers ===


def _compute_night_extension_until(
    now_utc: datetime,
    tz: ZoneInfo,
    minutes_to_grant: int,
    lock_cfg: dict,
    current_extension_utc: datetime | None = None,
) -> tuple[datetime, datetime]:
    """Bonus = "ile minut grania PODCZAS ciszy nocnej". Pierwszy grant liczy się od początku
    ciszy nocnej (trwającej albo najbliższej). Kolejne granty (gdy istnieje aktywne extension) DODAJĄ
    się do końca poprzedniego extension — wielokrotne klikanie "+60 min" akumuluje się
    (3 × +60 = 3h przedłużenia, nie 1h).

    Which night: the one in progress or the next (lock_logic.current_or_next_night_start), so after
    midnight it is the night of the previous evening (#48).

    Returns: (extension_utc, extension_local_for_display)
    """
    now_local = now_utc.astimezone(tz)

    # Baza do dopisania bonusu: aktualny extension jeśli istnieje i jest w przyszłości,
    # inaczej max(teraz, początek nocy).
    # max() chroni przed ustawieniem extension w przeszłości — gdy poprzedni extension
    # wygasł (np. był do 22:30, teraz 22:35), base = teraz, nie początek nocy.
    if current_extension_utc is not None and current_extension_utc > now_utc:
        base_local = current_extension_utc.astimezone(tz)
    else:
        night_start_dt = current_or_next_night_start(lock_cfg, now_local).replace(tzinfo=tz)
        base_local = max(now_local, night_start_dt)

    extension_local = base_local + timedelta(minutes=minutes_to_grant)
    return extension_local.astimezone(timezone.utc), extension_local


# === Endpoint ===


@router.post("/v1/totp_attempt")
async def totp_attempt(
    body: TotpAttemptRequest,
    device: Device = Depends(authenticate_request),
    db: sqlite3.Connection = Depends(get_db),
):
    code = body.code
    minutes_to_grant = body.minutes_to_grant
    ids = {"child_id": device.child_id, "device_id": device.id}

    now_utc = datetime.now(timezone.utc)
    tz = ZoneInfo(get_config_value(db, "timezone"))

    # === 0. TOTP enabled check — spójnie z heartbeat.py: sprawdzamy sekret ===
    totp_secret = get_config_value(db, "totp_secret") or ""
    if not totp_secret:
        return JSONResponse({"ok": False, "reason": "totp_not_configured"}, status_code=200)
    # === 1. Lockout check ===
    lockout_remaining = _check_lockout(db, now_utc)
    if lockout_remaining is not None:
        return JSONResponse(
            status_code=429,
            content={
                "ok": False,
                "reason": "lockout",
                "lockout_seconds_remaining": lockout_remaining,
            },
        )

    # === 2. TOTP verify: ±1 step, and the step must be newer than the last accepted one (no replay) ===
    step = match_totp_step(totp_secret, code, now_utc)
    last_step = int(get_config_value(db, "totp_last_step") or 0)
    if step is None or step <= last_step:
        new_count, seconds_remaining = _record_failure(db, now_utc)
        log_event(db, "totp_failed", {"attempts_count": new_count, "replay": step is not None}, **ids)
        if seconds_remaining:
            log_event(db, "totp_lockout", {"attempts_count": new_count, "seconds": seconds_remaining}, **ids)
        return JSONResponse(
            status_code=401,
            content={
                "ok": False,
                "reason": "invalid_code",
                "lockout_seconds_remaining": seconds_remaining,
            },
        )

    # === 3. TOTP OK — remember the step, reset lockout ===
    set_config_value(db, "totp_last_step", step)
    _reset_lockout(db)
    log_event(db, "totp_success", None, **ids)

    # === 4. Mode from the current lock of the device's child ===
    today = child_today(db, get_child(db, device.child_id), now_utc, tz)
    current_lock, state, today_local = today["lock_reason"], today["state"], today["today"]

    if current_lock == "limit":
        new_override = state["limit_override_minutes"] + minutes_to_grant
        update_child_day(db, device.child_id, today_local, limit_override_minutes=new_override)
        log_event(
            db, "grant_applied",
            {"mode": "extend_limit", "minutes": minutes_to_grant, "new_override": new_override}, **ids,
        )
        return {
            "ok": True,
            "mode": "extend_limit",
            "minutes": minutes_to_grant,
        }

    if current_lock == "night":
        # Stackowanie: wielokrotne TOTP-grant'y dodają się do końca poprzedniego extension.
        current_ext_utc = None
        if state.get("night_extension_until_utc"):
            try:
                parsed = parse_iso_utc(state["night_extension_until_utc"])
                if parsed > now_utc:
                    current_ext_utc = parsed
            except (ValueError, TypeError):
                pass
        ext_utc, ext_local = _compute_night_extension_until(
            now_utc, tz, minutes_to_grant, today["cfg"], current_extension_utc=current_ext_utc
        )
        update_child_day(db, device.child_id, today_local, night_extension_until_utc=ext_utc.isoformat())
        log_event(
            db, "grant_applied",
            {"mode": "extend_night", "minutes": minutes_to_grant, "until_local": ext_local.strftime("%Y-%m-%d %H:%M")},
            **ids,
        )
        return {
            "ok": True,
            "mode": "extend_night",
            "minutes": minutes_to_grant,
            "until_local": ext_local.strftime("%Y-%m-%d %H:%M"),
        }

    if current_lock == "manual":
        # Spec nie precyzuje tego case'u (sekcja 4.4 wymienia tylko limit/night).
        # Defensywna interpretacja: TOTP w trybie manual_lock = rodzic zdejmuje blokadę.
        update_child_day(db, device.child_id, today_local, manual_lock=0)
        log_event(db, "manual_unlock", {"via": "totp"}, **ids)
        return {
            "ok": True,
            "mode": "manual_unlock",
        }

    # current_lock is None — TOTP bez aktywnej blokady (nie powinno się zdarzyć w normalnym flow)
    return {
        "ok": True,
        "mode": "noop",
    }
