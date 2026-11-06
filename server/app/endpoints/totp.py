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
from datetime import date, datetime, timedelta, timezone
from zoneinfo import ZoneInfo

import pyotp
from fastapi import APIRouter, Depends, HTTPException, Request
from fastapi.responses import JSONResponse

from ..auth import authenticate_request
from ..db import (
    ensure_daily_state,
    get_config_value,
    get_db,
    log_event,
    update_daily_state,
)
from ..lock_logic import (
    compute_lock_reason,
    load_lock_config,
    parse_hhmm,
    parse_iso_utc,
)

router = APIRouter()

# Lockout schedule (sekcja 5.4): (cumulative_attempts, lockout_seconds)
_LOCKOUT_SCHEDULE: list[tuple[int, int]] = [
    (3, 60),       # 3 błędy → 60s
    (6, 300),      # 6 → 5 min
    (9, 1800),     # 9 → 30 min
    (12, 86400),   # 12 → 24h
]
_RESET_AFTER_SECONDS = 86400  # 24h bez prób → reset licznika (sekcja 5.4)


# === Lockout management ===
# Single-tenant: pojedynczy rekord w totp_lockout (id=1).


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
    for threshold, seconds in _LOCKOUT_SCHEDULE:
        if new_count == threshold:
            new_lockout_until = now_utc + timedelta(seconds=seconds)
            break

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


def _validate_request_body(body) -> dict:
    if not isinstance(body, dict):
        raise HTTPException(status_code=422, detail="body_must_be_object")
    code = body.get("code")
    if not isinstance(code, str) or not code.isdigit() or len(code) != 6:
        raise HTTPException(status_code=422, detail="missing_or_invalid: code")
    # minutes_to_grant jest OPCJONALNE — default 60 (godzina). Klient może w ogóle nie wysłać.
    # Dla backwards compat akceptujemy też explicit value w zakresie 1-1440.
    minutes = body.get("minutes_to_grant")
    if minutes is None:
        body["minutes_to_grant"] = 60
    elif isinstance(minutes, bool) or not isinstance(minutes, int) or minutes <= 0 or minutes > 1440:
        raise HTTPException(status_code=422, detail="invalid: minutes_to_grant must be 1-1440 or omitted")
    return body


def _get_current_lock_state(
    conn: sqlite3.Connection, now_utc: datetime, tz: ZoneInfo
) -> tuple[str | None, dict, date]:
    """Pobiera daily_state i wyznacza lock_reason. Zwraca (lock_reason, state_dict, today_local)."""
    today_local = now_utc.astimezone(tz).date()
    state = dict(ensure_daily_state(conn, today_local))
    lock_reason = compute_lock_reason(now_utc, tz, today_local, state, load_lock_config(conn))
    return lock_reason, state, today_local


def _compute_night_extension_until(
    now_utc: datetime,
    tz: ZoneInfo,
    minutes_to_grant: int,
    conn: sqlite3.Connection,
    current_extension_utc: datetime | None = None,
) -> tuple[datetime, datetime]:
    """Bonus = "ile minut grania PODCZAS ciszy nocnej". Pierwszy grant liczy się od
    night_lock_start. Kolejne granty (gdy istnieje aktywne extension) DODAJĄ się do
    końca poprzedniego extension — wielokrotne klikanie "+60 min" akumuluje się
    (3 × +60 = 3h przedłużenia, nie 1h).

    Cross-midnight handling: jeśli wołane już po północy ale przed night_end,
    night_lock_start odnosi się do wczorajszej daty.

    Returns: (extension_utc, extension_local_for_display)
    """
    night_start = parse_hhmm(get_config_value(conn, "night_lock_start"))
    night_end = parse_hhmm(get_config_value(conn, "night_lock_end"))
    now_local = now_utc.astimezone(tz)

    # Baza do dopisania bonusu: aktualny extension jeśli istnieje i jest w przyszłości,
    # inaczej max(teraz, dzisiejszy night_lock_start).
    # max() chroni przed ustawieniem extension w przeszłości — gdy poprzedni extension
    # wygasł (np. był do 22:30, teraz 22:35), base = teraz, nie night_start.
    if current_extension_utc is not None and current_extension_utc > now_utc:
        base_local = current_extension_utc.astimezone(tz)
    else:
        # Cross-midnight detection: window 21:30→06:30, jesteśmy o 02:00 → night zaczął się wczoraj
        if night_start > night_end and now_local.time() < night_end:
            night_start_date = now_local.date() - timedelta(days=1)
        else:
            night_start_date = now_local.date()
        night_start_dt = datetime.combine(night_start_date, night_start, tzinfo=tz)
        # Jeśli cisza nocna już trwa, liczymy grant od teraz (nie od dawno minionego startu)
        base_local = max(now_local, night_start_dt)

    extension_local = base_local + timedelta(minutes=minutes_to_grant)
    return extension_local.astimezone(timezone.utc), extension_local


# === Endpoint ===


@router.post("/v1/totp_attempt")
async def totp_attempt(
    request: Request,
    _: None = Depends(authenticate_request),
    db: sqlite3.Connection = Depends(get_db),
):
    raw_body = await request.json()
    body = _validate_request_body(raw_body)

    code = body["code"]
    minutes_to_grant = body["minutes_to_grant"]

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

    # === 2. TOTP verify (window=1 → akceptuje T-1, T, T+1, czyli ±30s; spec 5.4) ===
    # totp_secret już pobrany w sekcji 0
    totp = pyotp.TOTP(totp_secret)
    if not totp.verify(code, valid_window=1):
        new_count, seconds_remaining = _record_failure(db, now_utc)
        log_event(db, "totp_failed", {"attempts_count": new_count})
        return JSONResponse(
            status_code=401,
            content={
                "ok": False,
                "reason": "invalid_code",
                "lockout_seconds_remaining": seconds_remaining,
            },
        )

    # === 3. TOTP OK — reset lockout ===
    _reset_lockout(db)
    log_event(db, "totp_success", None)

    # === 4. Określ tryb na podstawie aktualnego lock_reason ===
    current_lock, state, today_local = _get_current_lock_state(db, now_utc, tz)

    if current_lock == "limit":
        new_override = state["limit_override_minutes"] + minutes_to_grant
        update_daily_state(db, today_local, limit_override_minutes=new_override)
        log_event(
            db, "grant_applied",
            {"mode": "extend_limit", "minutes": minutes_to_grant, "new_override": new_override},
        )
        return {
            "ok": True,
            "mode": "extend_limit",
            "applied": f"Dodano {minutes_to_grant} minut do limitu dziennego.",
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
            now_utc, tz, minutes_to_grant, db, current_extension_utc=current_ext_utc
        )
        update_daily_state(
            db, today_local, night_extension_until_utc=ext_utc.isoformat()
        )
        log_event(
            db, "grant_applied",
            {"mode": "extend_night", "minutes": minutes_to_grant, "until_local": ext_local.strftime("%Y-%m-%d %H:%M")},
        )
        return {
            "ok": True,
            "mode": "extend_night",
            "applied": f"Dziecko może grać do {ext_local.strftime('%H:%M')}.",
        }

    if current_lock == "manual":
        # Spec nie precyzuje tego case'u (sekcja 4.4 wymienia tylko limit/night).
        # Defensywna interpretacja: TOTP w trybie manual_lock = rodzic zdejmuje blokadę.
        update_daily_state(db, today_local, manual_lock=0)
        log_event(db, "manual_unlock", {"via": "totp"})
        return {
            "ok": True,
            "mode": "manual_unlock",
            "applied": "Manualna blokada zdjęta.",
        }

    # current_lock is None — TOTP bez aktywnej blokady (nie powinno się zdarzyć w normalnym flow)
    return {
        "ok": True,
        "mode": "noop",
        "applied": "Brak aktywnej blokady — kod TOTP zignorowany.",
    }
