"""Kreator pierwszego uruchomienia (/setup).

GET  /setup           — strona wizarda (serwuje setup.html)
GET  /setup/totp_qr   — QR code TOTP dla wizarda (tylko gdy setup_complete == 0)
POST /setup/complete  — finalizuje konfigurację, ustawia setup_complete + init_open
"""

from __future__ import annotations

import io
import re
import sqlite3
from pathlib import Path
from zoneinfo import ZoneInfo, ZoneInfoNotFoundError

import pyotp
import qrcode
from fastapi import APIRouter, Depends, HTTPException, Request
from fastapi.responses import HTMLResponse, JSONResponse, RedirectResponse

from ..admin_auth import issue_session_cookie, set_session_cookie
from ..config import TOTP_ISSUER
from ..crypto import hash_password
from ..db import get_config_value, get_db, log_event

router = APIRouter()

_STATIC_DIR = Path(__file__).parent.parent / "static"
_HHMM_RE = re.compile(r'^([01]\d|2[0-3]):[0-5]\d$')


def _png_qr_base64(data: str) -> str:
    import base64

    from qrcode.image.pil import PilImage
    qr = qrcode.QRCode(version=None, border=2, box_size=8,
                       error_correction=qrcode.constants.ERROR_CORRECT_M)
    qr.add_data(data)
    qr.make(fit=True)
    img = qr.make_image(image_factory=PilImage, fill_color="black", back_color="white")
    buf = io.BytesIO()
    img.save(buf, format="PNG")
    return base64.b64encode(buf.getvalue()).decode("ascii")


@router.get("/setup", response_class=HTMLResponse)
async def setup_page(db: sqlite3.Connection = Depends(get_db)) -> HTMLResponse:
    if get_config_value(db, "setup_complete") == "1":
        return RedirectResponse("/admin", status_code=302)
    return HTMLResponse((_STATIC_DIR / "setup.html").read_text(encoding="utf-8"))


@router.get("/setup/totp_qr")
async def setup_totp_qr(db: sqlite3.Connection = Depends(get_db)) -> dict:
    """Zwraca QR TOTP tylko podczas wizarda (setup_complete == 0)."""
    if get_config_value(db, "setup_complete") == "1":
        raise HTTPException(status_code=404, detail="setup_already_complete")
    secret = get_config_value(db, "totp_secret")
    uri = pyotp.TOTP(secret).provisioning_uri(name="rodzic", issuer_name=TOTP_ISSUER)
    return {"qr_png_base64": _png_qr_base64(uri), "uri": uri}


@router.post("/setup/complete")
async def setup_complete(request: Request,
                         db: sqlite3.Connection = Depends(get_db)) -> JSONResponse:
    if get_config_value(db, "setup_complete") == "1":
        raise HTTPException(status_code=409, detail="already_configured")

    body = await request.json()

    # --- Walidacja ---
    device_name       = str(body.get("device_name", "")).strip()
    password          = str(body.get("password", ""))
    totp_code         = str(body.get("totp_code", "")).strip()
    timezone_str      = str(body.get("timezone", "Europe/Warsaw"))
    limit_weekday     = body.get("daily_limit_weekday", 120)
    limit_weekend     = body.get("daily_limit_weekend", 240)
    night_start       = str(body.get("night_lock_start", "21:30"))
    night_end         = str(body.get("night_lock_end",   "06:30"))
    watchdog_server_url = str(body.get("watchdog_server_url", "")).strip()

    errors = {}
    if not device_name:
        errors["device_name"] = "required"
    if len(password) < 8:
        errors["password"] = "min_8_chars"
    if not watchdog_server_url or not watchdog_server_url.startswith(("http://", "https://")):
        errors["watchdog_server_url"] = "required_http_url"
    if not _HHMM_RE.match(night_start):
        errors["night_lock_start"] = "invalid_hhmm"
    if not _HHMM_RE.match(night_end):
        errors["night_lock_end"] = "invalid_hhmm"
    try:
        ZoneInfo(timezone_str)
    except (ZoneInfoNotFoundError, KeyError):
        errors["timezone"] = "invalid"
    try:
        limit_weekday = max(0, int(limit_weekday))
        limit_weekend = max(0, int(limit_weekend))
    except (TypeError, ValueError):
        errors["limits"] = "must_be_integers"
    if errors:
        raise HTTPException(status_code=422, detail=errors)

    # --- TOTP (opcjonalne) ---
    if totp_code:
        # Weryfikacja: użytkownik podał kod — sprawdź czy pasuje do sekretu
        secret = get_config_value(db, "totp_secret")
        if not pyotp.TOTP(secret).verify(totp_code, valid_window=1):
            raise HTTPException(status_code=422, detail={"totp_code": "invalid"})

    # --- Zapis ---
    settings = [
        ("device_name",          device_name),
        ("parent_password_hash", hash_password(password)),
        ("timezone",             timezone_str),
        ("daily_limit_weekday",  str(limit_weekday)),
        ("daily_limit_weekend",  str(limit_weekend)),
        ("night_lock_start",     night_start),
        ("night_lock_end",       night_end),
        ("watchdog_server_url",  watchdog_server_url),
        ("init_open",            "1"),
        ("setup_complete",       "1"),
    ]
    for key, value in settings:
        db.execute("INSERT OR REPLACE INTO config(key, value) VALUES (?, ?)", (key, value))

    log_event(db, "setup_completed",
              {"device_name": device_name})

    # Auto-login: wystawiamy cookie sesji żeby JS mógł od razu przejść do /admin
    cookie_value = issue_session_cookie(db)
    response = JSONResponse({"ok": True})
    set_session_cookie(response, cookie_value)
    return response
