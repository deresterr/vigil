"""JSON API dla admin UI (/admin/api/*). Wszystkie wymagają cookie session (require_admin_session).

Endpointy:
- GET  /admin/api/status            — current daily_state + lock + watchdog online + config snapshot
- POST /admin/api/grant             — manual grant czasu (limit lub night)
- POST /admin/api/manual_lock       — toggle manual_lock
- POST /admin/api/clear_extension   — wyczyść night_extension_until_utc
- GET  /admin/api/config            — wszystkie config values (oprócz secrets)
- POST /admin/api/config            — update config (single key lub batch)
- POST /admin/api/regen_totp        — wygeneruj nowy TOTP secret + zwróć QR
- POST /admin/api/regen_tokens      — wygeneruj nowy bearer + signing_key dla klienta
- GET  /admin/api/events            — lista ostatnich N events
- POST /admin/api/change_password   — zmiana hasła rodzica
"""

from __future__ import annotations

import io
import re
import sqlite3
from datetime import datetime, timedelta, timezone
from typing import Any
from zoneinfo import ZoneInfo

import pyotp
import qrcode
from fastapi import APIRouter, Depends, HTTPException, Request

from ..admin_auth import require_admin_session, verify_admin_password
from ..auth import invalidate_auth_cache
from ..endpoints.heartbeat import invalidate_heartbeat_config_cache
from ..config import TOTP_ISSUER
from ..crypto import generate_token_b64, hash_password
from ..db import (
    get_connection,
    ensure_daily_state,
    get_config_int,
    get_config_value,
    get_db,
    log_event,
    set_config_value,
    update_daily_state,
)
from ..lock_logic import (
    compute_lock_reason,
    is_in_night_window,
    is_weekend,
    load_lock_config,
    parse_hhmm,
    parse_iso_utc,
)
from .totp import _compute_night_extension_until

router = APIRouter(prefix="/admin/api", dependencies=[Depends(require_admin_session)])


# === GET /admin/api/status ===


@router.get("/status")
async def status(db: sqlite3.Connection = Depends(get_db)) -> dict:
    """Podstawowy status — wszystko czego admin UI potrzebuje do top sekcji.

    Auto-refresh co 5s po stronie klienta.
    """
    now_utc = datetime.now(timezone.utc)
    tz_name = get_config_value(db, "timezone")
    tz = ZoneInfo(tz_name)
    now_local = now_utc.astimezone(tz)
    today_local = now_local.date()

    state = dict(ensure_daily_state(db, today_local))

    # Night extension — potrzebne zarówno do lock_reason jak i do wyświetlenia "do kiedy"
    night_extended = False
    night_extension_local: str | None = None
    if state["night_extension_until_utc"]:
        ext_until = parse_iso_utc(state["night_extension_until_utc"])
        if now_utc < ext_until:
            night_extended = True
        night_extension_local = ext_until.astimezone(tz).strftime("%Y-%m-%d %H:%M")

    lock_reason: str | None = compute_lock_reason(now_utc, tz, today_local, state, load_lock_config(db))

    # in_night potrzebne do pola in_night_window w response (informacyjne dla admin UI)
    night_start = parse_hhmm(get_config_value(db, "night_lock_start"))
    night_end = parse_hhmm(get_config_value(db, "night_lock_end"))
    in_night = is_in_night_window(now_local.time(), night_start, night_end)

    daily_limit = get_config_int(
        db, "daily_limit_weekend" if is_weekend(today_local) else "daily_limit_weekday"
    )
    effective_limit = daily_limit + state["limit_override_minutes"]
    minutes_used = state["seconds_used"] // 60

    # Watchdog online: ostatni heartbeat <= grace_seconds temu
    watchdog_online = False
    last_heartbeat_ago: int | None = None
    if state["last_heartbeat_utc"]:
        last_hb = parse_iso_utc(state["last_heartbeat_utc"])
        last_heartbeat_ago = int((now_utc - last_hb).total_seconds())
        grace = get_config_int(db, "heartbeat_grace_seconds")
        watchdog_online = last_heartbeat_ago <= grace * 2  # 2× grace dla "online" w UI (bardziej tolerancyjne)

    return {
        "device_name": get_config_value(db, "device_name") or "Vigil",
        "today_local": today_local.isoformat(),
        "is_weekend": is_weekend(today_local),
        "current_time_local": now_local.strftime("%Y-%m-%d %H:%M:%S"),
        "lock_required": lock_reason is not None,
        "lock_reason": lock_reason,
        "minutes_used": minutes_used,
        "minutes_remaining": max(0, effective_limit - minutes_used),
        "daily_limit": daily_limit,
        "limit_override_minutes": state["limit_override_minutes"],
        "effective_limit": effective_limit,
        "manual_lock": bool(state["manual_lock"]),
        "pause_active": bool(state.get("pause_active", 0)),
        "night_extension_until_local": night_extension_local if night_extended else None,
        "in_night_window": in_night,
        "night_lock_start": get_config_value(db, "night_lock_start"),
        "night_lock_end": get_config_value(db, "night_lock_end"),
        # Watchdog state
        "watchdog_online": watchdog_online,
        "last_heartbeat_utc": state["last_heartbeat_utc"],
        "last_heartbeat_ago_seconds": last_heartbeat_ago,
        "init_open": get_config_value(db, "init_open") == "1",
    }


# === POST /admin/api/grant ===


@router.post("/grant")
async def grant(request: Request, db: sqlite3.Connection = Depends(get_db)) -> dict:
    """Manualny grant czasu (bez TOTP — admin już zalogowany hasłem).

    Body: {"minutes": int, "mode": "limit" | "night"}
    """
    body = await request.json()
    if not isinstance(body, dict):
        raise HTTPException(status_code=422, detail="body_must_be_object")
    minutes = body.get("minutes")
    mode = body.get("mode")
    if not isinstance(minutes, int) or isinstance(minutes, bool) or minutes <= 0 or minutes > 1440:
        raise HTTPException(status_code=422, detail="invalid_minutes")
    if mode not in ("limit", "night"):
        raise HTTPException(status_code=422, detail="invalid_mode")

    now_utc = datetime.now(timezone.utc)
    tz = ZoneInfo(get_config_value(db, "timezone"))
    today_local = now_utc.astimezone(tz).date()
    # Upsert pustego rekordu (idempotent) — kolejne SQL operuje na istniejącym rekordzie
    ensure_daily_state(db, today_local)

    if mode == "limit":
        # Atomic increment — race-safe gdy admin szybko klika kilka grantów równolegle.
        # Stary kod (SELECT → modify in Python → UPDATE) tracił grants w tym scenariuszu.
        db.execute(
            "UPDATE daily_state SET limit_override_minutes = limit_override_minutes + ? "
            "WHERE date_local = ?",
            (minutes, today_local.isoformat()),
        )
        # Re-SELECT żeby zwrócić aktualną wartość w response (informational)
        new_override = db.execute(
            "SELECT limit_override_minutes FROM daily_state WHERE date_local = ?",
            (today_local.isoformat(),),
        ).fetchone()["limit_override_minutes"]
        log_event(
            db, "grant_applied",
            {"mode": "extend_limit", "minutes": minutes, "via": "admin_ui"},
        )
        return {
            "ok": True,
            "applied": f"Dodano {minutes} minut do limitu dziennego.",
            "new_override": new_override,
        }

    # mode == "night"
    # Czytamy AKTUALNY extension żeby się dopisać do końca poprzedniego (jeśli istnieje).
    # Stary kod liczył zawsze od night_start → wielokrotne klikanie "+60" dawało zawsze
    # ten sam moment końca (nadpisanie, nie akumulacja).
    current_ext_row = db.execute(
        "SELECT night_extension_until_utc FROM daily_state WHERE date_local = ?",
        (today_local.isoformat(),),
    ).fetchone()
    current_ext_utc = None
    if current_ext_row and current_ext_row["night_extension_until_utc"]:
        try:
            parsed = parse_iso_utc(current_ext_row["night_extension_until_utc"])
            if parsed > now_utc:
                current_ext_utc = parsed
        except (ValueError, TypeError):
            pass  # corrupt data — ignoruj, bazuj na night_start

    ext_utc, ext_local = _compute_night_extension_until(
        now_utc, tz, minutes, db, current_extension_utc=current_ext_utc
    )
    update_daily_state(
        db, today_local, night_extension_until_utc=ext_utc.isoformat()
    )
    log_event(
        db, "grant_applied",
        {"mode": "extend_night", "minutes": minutes, "via": "admin_ui",
         "until_local": ext_local.strftime("%Y-%m-%d %H:%M")},
    )
    return {
        "ok": True,
        "applied": f"Dziecko może grać do {ext_local.strftime('%H:%M')}.",
        "until_local": ext_local.strftime("%Y-%m-%d %H:%M"),
    }


# === POST /admin/api/manual_lock ===


@router.post("/manual_lock")
async def set_manual_lock(request: Request, db: sqlite3.Connection = Depends(get_db)) -> dict:
    """Body: {"locked": true|false}. Toggle blokady manualnej."""
    body = await request.json()
    if not isinstance(body, dict) or not isinstance(body.get("locked"), bool):
        raise HTTPException(status_code=422, detail="invalid_body")

    now_utc = datetime.now(timezone.utc)
    tz = ZoneInfo(get_config_value(db, "timezone"))
    today_local = now_utc.astimezone(tz).date()
    ensure_daily_state(db, today_local)

    new_state = 1 if body["locked"] else 0
    update_daily_state(db, today_local, manual_lock=new_state)
    log_event(
        db, "manual_lock_set" if body["locked"] else "manual_lock_cleared",
        {"via": "admin_ui"},
    )
    return {"ok": True, "manual_lock": bool(new_state)}


# === POST /admin/api/clear_extension ===


@router.post("/clear_extension")
async def clear_extension(db: sqlite3.Connection = Depends(get_db)) -> dict:
    """Wyczysz wszystkie przedłużenia: night_extension i limit_override (bonus czasu).
    Przydatne gdy rodzic się rozmyślił po przyznaniu +60 lub +godziny ciszy nocnej."""
    now_utc = datetime.now(timezone.utc)
    tz = ZoneInfo(get_config_value(db, "timezone"))
    today_local = now_utc.astimezone(tz).date()
    update_daily_state(
        db, today_local,
        night_extension_until_utc=None,
        limit_override_minutes=0,
    )
    log_event(db, "extensions_cleared", {"via": "admin_ui"})
    return {"ok": True}


# === GET /admin/api/config ===

# Klucze edytowalne przez admin UI (kosmetyczne typu retention zostają hardcoded).
_EDITABLE_CONFIG_KEYS = {
    "daily_limit_weekday": int,
    "daily_limit_weekend": int,
    "night_lock_start": str,  # HH:MM
    "night_lock_end": str,    # HH:MM
    "timezone": str,
    # heartbeat_grace_seconds: używany TYLKO przez admin UI do wyznaczania "watchdog online".
    # Watchdog (Worker.cs) hardkoduje własny grace (30s po starcie, 90s podczas startup).
    # Zmiana tej wartości NIE wpływa na zachowanie watchdoga.
    "heartbeat_grace_seconds": int,
}

# Dopuszczalne zakresy dla integer config values. Klucze bez wpisu mają tylko `value >= 0`.
# 1440 min = 24h (max dziennego limitu); idle/grace celowo nie nakładamy aby admin nie
# wpadł na ścianę w niespotykanych setupach.
_INT_CONFIG_BOUNDS: dict[str, tuple[int, int]] = {
    "daily_limit_weekday": (0, 1440),
    "daily_limit_weekend": (0, 1440),
}

# Strict HH:MM 24h: dwie cyfry godziny 00-23, dwie cyfry minuty 00-59. Bez whitespace,
# bez sekund, bez AM/PM. Frontend wysyła z <input type="time"> więc format jest gwarantowany,
# ale walidujemy też tutaj — API może być wołane spoza UI.
_HHMM_RE = re.compile(r"^([01]\d|2[0-3]):[0-5]\d$")


@router.get("/config")
async def get_config(db: sqlite3.Connection = Depends(get_db)) -> dict:
    """Zwraca tylko edytowalne klucze (bez secrets typu hasło/TOTP/session_key)."""
    result = {}
    for key in _EDITABLE_CONFIG_KEYS:
        val = get_config_value(db, key)
        if val is not None:
            result[key] = val
    return result


# === POST /admin/api/config ===


@router.post("/config")
async def update_config(request: Request, db: sqlite3.Connection = Depends(get_db)) -> dict:
    """Update jednego lub wielu kluczy. Body: {"key1": value1, "key2": value2, ...}.

    Walidacja (strict — wszelkie niezgodności → 422):
    - klucz musi być na whiteliście _EDITABLE_CONFIG_KEYS
    - typ wartości musi pasować (bool NIE liczy się jako int)
    - int: w zakresie z _INT_CONFIG_BOUNDS (jeśli klucz tam jest) lub >= 0
    - HH:MM: regex _HHMM_RE (00:00–23:59, bez whitespace, bez sekund)
    - timezone: valid IANA name (sprawdzane przez ZoneInfo)
    """
    body = await request.json()
    if not isinstance(body, dict):
        raise HTTPException(status_code=422, detail="body_must_be_object")

    updated: list[str] = []
    for key, value in body.items():
        if key not in _EDITABLE_CONFIG_KEYS:
            raise HTTPException(status_code=422, detail=f"unknown_key: {key}")

        expected_type = _EDITABLE_CONFIG_KEYS[key]
        if expected_type is int:
            # bool jest instance of int w Pythonie — odrzucamy żeby True nie poszedł
            # przypadkiem jako wartość 1 dla limit/timeout.
            if isinstance(value, bool) or not isinstance(value, int):
                raise HTTPException(status_code=422, detail=f"expected_int: {key}")
            bounds = _INT_CONFIG_BOUNDS.get(key)
            if bounds is not None:
                lo, hi = bounds
                if value < lo or value > hi:
                    raise HTTPException(
                        status_code=422,
                        detail=f"out_of_range: {key} must be {lo}..{hi}",
                    )
            elif value < 0:
                raise HTTPException(status_code=422, detail=f"negative_value: {key}")
            set_config_value(db, key, value)
        else:  # str
            if not isinstance(value, str):
                raise HTTPException(status_code=422, detail=f"expected_str: {key}")
            if key in ("night_lock_start", "night_lock_end"):
                if not _HHMM_RE.match(value):
                    raise HTTPException(
                        status_code=422,
                        detail=f"invalid_hhmm: {key} (expected HH:MM, 00:00-23:59)",
                    )
            if key == "timezone":
                try:
                    ZoneInfo(value)
                except Exception as e:
                    raise HTTPException(status_code=422, detail=f"invalid_timezone: {value}") from e
            set_config_value(db, key, value)

        updated.append(key)

    log_event(db, "config_updated", {"keys": updated, "via": "admin_ui"})
    # Unieważnij cache heartbeat — _get_hb_cfg() cachuje klucze config na 60s.
    # Bez tego zmiany (night_lock_start, daily_limit_*) działałyby z ~60s opóźnieniem.
    invalidate_heartbeat_config_cache()
    return {"ok": True, "updated": updated}


# === POST /admin/api/regen_totp ===


def _png_qr_base64(data: str) -> str:
    """Render QR jako PNG, zwraca base64 string (do data: URL w <img src>).

    Wymuszamy PIL backend (Pillow). Domyślnie qrcode.make_image() detect-uje czy Pillow
    jest dostępny i fallbackuje do PyPNGImage (pure-Python) który ma inną sygnaturę save().
    Pillow jest hard requirement w requirements.txt — jeśli go brakuje, ImportError jest
    bardziej czytelny niż obecny TypeError z save(format=...).
    """
    import base64
    from qrcode.image.pil import PilImage
    qr = qrcode.QRCode(
        version=None,
        border=2,
        box_size=8,
        error_correction=qrcode.constants.ERROR_CORRECT_M,
    )
    qr.add_data(data)
    qr.make(fit=True)
    img = qr.make_image(image_factory=PilImage, fill_color="black", back_color="white")
    buf = io.BytesIO()
    img.save(buf, format="PNG")
    return base64.b64encode(buf.getvalue()).decode("ascii")


# === GET /admin/api/totp — zwraca obecny TOTP secret + QR ===


@router.get("/totp")
async def get_totp(db: sqlite3.Connection = Depends(get_db)) -> dict:
    """Zwraca AKTUALNY (nie regenerowany) TOTP secret + QR — do wyświetlenia w admin UI
    żeby rodzic mógł zeskanować ponownie (np. po wymianie telefonu)."""
    secret = get_config_value(db, "totp_secret")
    if not secret:
        raise HTTPException(status_code=404, detail="totp_not_configured")
    uri = pyotp.TOTP(secret).provisioning_uri(name="rodzic", issuer_name=TOTP_ISSUER)
    return {
        "secret": secret,
        "uri": uri,
        "qr_png_base64": _png_qr_base64(uri),
    }


@router.post("/regen_totp")
async def regen_totp(db: sqlite3.Connection = Depends(get_db)) -> dict:
    """Generuje nowy TOTP secret. Trzeba ponownie zeskanować QR aplikacją Aegis."""
    new_secret = pyotp.random_base32()
    set_config_value(db, "totp_secret", new_secret)
    # Reset lockoutu — żeby admin nie musiał czekać po regeneracji
    db.execute("DELETE FROM totp_lockout")
    # totp_secret jest w _HB_CFG_KEYS — inwaliduj cache żeby nowy secret był widoczny od razu
    invalidate_heartbeat_config_cache()

    uri = pyotp.TOTP(new_secret).provisioning_uri(name="rodzic", issuer_name=TOTP_ISSUER)
    log_event(db, "totp_regenerated", {"via": "admin_ui"})

    return {
        "ok": True,
        "secret": new_secret,
        "uri": uri,
        "qr_png_base64": _png_qr_base64(uri),
    }


# === GET /admin/api/tokens — zwraca obecne bearer + signing_key dla klienta ===


@router.get("/tokens")
async def get_tokens(db: sqlite3.Connection = Depends(get_db)) -> dict:
    """Zwraca AKTUALNE tokeny watchdoga — do wyświetlenia w UI gdy rodzic chce skopiować
    je do Install.cmd lub vigil.toml. UWAGA: signing_key to secret, nie wycieka poza
    sesję admin."""
    bearer = get_config_value(db, "bearer_token")
    signing = get_config_value(db, "signing_key")
    if not bearer or not signing:
        raise HTTPException(status_code=404, detail="tokens_not_found")
    return {
        "bearer_token": bearer,
        "signing_key": signing,
    }


# === POST /admin/api/regen_tokens ===


@router.post("/regen_tokens")
async def regen_tokens() -> dict:
    """Generuje nowe bearer + signing_key. Po tym vigil.toml na dziecięcym PC wymaga
    aktualizacji — bez tego watchdog przestanie się autoryzować.

    Używa get_connection() bezpośrednio zamiast Depends(get_db) — invalidate_auth_cache()
    musi być wywołany PO commicie, żeby żaden heartbeat nie wypełnił cache starymi tokenami.
    """
    new_bearer      = generate_token_b64(32)
    new_signing_key = generate_token_b64(32)

    with get_connection() as db:
        set_config_value(db, "bearer_token",  new_bearer)
        set_config_value(db, "signing_key",   new_signing_key)
        log_event(db, "tokens_regenerated", {"via": "admin_ui"})
    # ← commit tutaj — nowe tokeny są w DB

    # Invalidacja DUPĂ commicie: żaden heartbeat nie wpadnie w okno między
    # invalidate a commit i nie wypełni cache starymi tokenami.
    invalidate_auth_cache()

    snippet = (
        f'[server]\n'
        f'  bearer_token = "{new_bearer}"\n'
        f'  signing_key  = "{new_signing_key}"\n'
    )

    return {
        "ok": True,
        "bearer_token": new_bearer,
        "signing_key": new_signing_key,
        "config_snippet": snippet,
    }


# === GET /admin/api/events ===


@router.get("/events")
async def list_events(
    db: sqlite3.Connection = Depends(get_db),
    limit: int = 50,
) -> dict:
    """Ostatnie N events, najnowsze pierwsze."""
    if limit < 1 or limit > 500:
        raise HTTPException(status_code=422, detail="limit_out_of_range")
    rows = db.execute(
        "SELECT id, ts_utc, event_type, details_json "
        "FROM events ORDER BY id DESC LIMIT ?",
        (limit,),
    ).fetchall()
    return {
        "events": [
            {
                "id": r["id"],
                "ts_utc": r["ts_utc"],
                "event_type": r["event_type"],
                "details_json": r["details_json"],
            }
            for r in rows
        ]
    }


# === POST /admin/api/change_password ===


@router.post("/change_password")
async def change_password(request: Request, db: sqlite3.Connection = Depends(get_db)) -> dict:
    """Body: {"current": "...", "new": "..."}.

    Nie unieważniamy istniejących cookie sessions (admin_session_key osobny od hasła).
    Możemy to zmienić na bardziej restrykcyjne w przyszłości — na razie KISS.
    """
    body = await request.json()
    if not isinstance(body, dict):
        raise HTTPException(status_code=422, detail="body_must_be_object")
    current = body.get("current")
    new = body.get("new")
    if not isinstance(current, str) or not isinstance(new, str):
        raise HTTPException(status_code=422, detail="missing_fields")
    if len(new) < 8:
        raise HTTPException(status_code=422, detail="new_password_too_short")

    if not verify_admin_password(db, current):
        log_event(db, "admin_password_change_failed")
        raise HTTPException(status_code=401, detail="wrong_current_password")

    set_config_value(db, "parent_password_hash", hash_password(new))
    log_event(db, "admin_password_changed")
    return {"ok": True}


# === GET /admin/api/init_status + POST /admin/api/enable_init_mode ===


@router.get("/init_status")
async def init_status(db: sqlite3.Connection = Depends(get_db)) -> dict:
    """Status trybu inicjalizacji (redundantne z /status → init_open, zachowane dla API compat)."""
    return {
        "init_open": get_config_value(db, "init_open") == "1",
        "watchdog_server_url": get_config_value(db, "watchdog_server_url") or "",
    }


@router.post("/enable_init_mode")
async def enable_init_mode(db: sqlite3.Connection = Depends(get_db)) -> dict:
    """Ponownie otwiera tryb inicjalizacji — gdy trzeba zainstalować watchdoga drugi raz
    (np. nowy PC, reinstall systemu).
    Uruchom Install.cmd na PC dziecka — zamknie się po pierwszym heartbeat."""
    set_config_value(db, "init_open", "1")
    log_event(db, "init_reopened", {"via": "admin_ui"})
    return {"ok": True}
