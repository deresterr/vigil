"""First-run wizard (/setup), open without a login until it is completed.

GET  /setup           the wizard page (setup.html)
GET  /setup/totp_qr   the TOTP QR code (only while setup_complete == 0)
POST /setup/complete  saves everything and sets setup_complete
"""

from __future__ import annotations

import sqlite3
from datetime import datetime, timezone
from typing import Annotated
from zoneinfo import ZoneInfo, ZoneInfoNotFoundError

import pyotp
from fastapi import APIRouter, Body, Depends, HTTPException
from fastapi.responses import HTMLResponse, JSONResponse, RedirectResponse
from pydantic import BaseModel, ConfigDict, Field, StringConstraints, ValidationError, field_validator

from ..admin_auth import issue_session_cookie, set_session_cookie
from ..config import TOTP_ACCOUNT, TOTP_ISSUER
from ..crypto import hash_password, match_totp_step
from ..db import get_config_value, get_db, log_event
from ..pages import page
from .admin_api import ChildCreate, Name, Week, insert_child, png_qr_base64

router = APIRouter()


class SetupBody(BaseModel):
    """The wizard's form (setup.html). Lax on purpose: it may send a field the server no longer reads; every
    problem is reported per field (`detail`: field → code). The week is the panel's, day by day (#102)."""

    model_config = ConfigDict(extra="ignore")

    child_name: Annotated[Name, StringConstraints(strip_whitespace=True)]
    password: Annotated[str, Field(min_length=8)]  # as typed: no stripping
    totp_code: Annotated[str, StringConstraints(strip_whitespace=True)] = ""
    timezone: Annotated[str, StringConstraints(strip_whitespace=True)] = "UTC"
    schedule: Week

    @field_validator("timezone")
    @classmethod
    def _valid_tz(cls, v: str) -> str:
        try:
            ZoneInfo(v)
        except (ZoneInfoNotFoundError, ValueError) as e:
            raise ValueError("invalid") from e
        return v


def _field_errors(e: ValidationError) -> dict[str, str]:
    """Pydantic's errors as the wizard shows them: {field: code}, a day's field as `schedule.<day>.<field>`
    (Monday = 0; a day's own rule, an empty night, as `schedule.<day>`)."""
    errors: dict[str, str] = {}
    for err in e.errors():
        field = ".".join(str(part) for part in err["loc"]) or "body"
        errors[field] = str(err.get("ctx", {}).get("error", "")) or err["type"]
    return errors


@router.get("/setup", response_class=HTMLResponse)
async def setup_page(db: sqlite3.Connection = Depends(get_db)) -> HTMLResponse:
    if get_config_value(db, "setup_complete") == "1":
        return RedirectResponse("/admin", status_code=302)
    return HTMLResponse(page("setup.html"))


@router.get("/setup/totp_qr")
async def setup_totp_qr(db: sqlite3.Connection = Depends(get_db)) -> dict:
    """The TOTP QR code, only while the wizard is not complete."""
    if get_config_value(db, "setup_complete") == "1":
        raise HTTPException(status_code=404, detail="setup_already_complete")
    secret = get_config_value(db, "totp_secret")
    uri = pyotp.TOTP(secret).provisioning_uri(name=TOTP_ACCOUNT, issuer_name=TOTP_ISSUER)
    return {"qr_png_base64": png_qr_base64(uri), "uri": uri}


@router.post("/setup/complete")
async def setup_complete(raw: Annotated[object, Body()] = None,
                         db: sqlite3.Connection = Depends(get_db)) -> JSONResponse:
    if get_config_value(db, "setup_complete") == "1":
        raise HTTPException(status_code=409, detail="already_configured")
    try:
        body = SetupBody.model_validate(raw)
    except ValidationError as e:
        raise HTTPException(status_code=422, detail=_field_errors(e)) from e

    # --- TOTP (optional) ---
    if body.totp_code:
        # A code was typed: it must match the secret
        totp_step = match_totp_step(get_config_value(db, "totp_secret"), body.totp_code, datetime.now(timezone.utc))
        if totp_step is None:
            raise HTTPException(status_code=422, detail={"totp_code": "invalid"})
        # The code typed in the wizard must not work again on the lockscreen.
        db.execute("INSERT OR REPLACE INTO config(key, value) VALUES ('totp_last_step', ?)", (str(totp_step),))

    # --- Save ---
    settings = [
        ("parent_password_hash", hash_password(body.password)),
        ("timezone",             body.timezone),
        ("setup_complete",       "1"),
    ]
    for key, value in settings:
        db.execute("INSERT OR REPLACE INTO config(key, value) VALUES (?, ?)", (key, value))
    # The first child (D7) gets the week from the wizard; its PCs are paired from the panel.
    child_id = insert_child(db, ChildCreate(name=body.child_name, schedule=body.schedule))

    log_event(db, "setup_completed", {"child_name": body.child_name}, child_id=child_id)

    # Logged in at once: the page goes straight to /admin
    response = JSONResponse({"ok": True})
    set_session_cookie(response, issue_session_cookie(db))
    return response
