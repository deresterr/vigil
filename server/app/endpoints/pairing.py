"""Pairing a child PC with the server using a one-time code (replaces the open claim_credentials).

Flow: the parent generates a code in the panel (POST /admin/api/pairing_code), the installer on the
child PC sends it to POST /v1/pair and receives a freshly generated signing key + server URL.

- 8 digits, valid 10 minutes, single use, 5 wrong attempts invalidate it (guessing odds ~5e-8).
- Every successful pairing rotates the signing key, so a previously paired PC stops authenticating.
- Only a hash of the code is stored.

Protection against other LAN devices grabbing the key. The response travels over plain HTTP on the LAN
like every other request; use a reverse proxy with TLS if that matters in your network.
"""

from __future__ import annotations

import hashlib
import hmac
import secrets
import sqlite3
from datetime import datetime, timedelta, timezone

from fastapi import APIRouter, Depends, HTTPException, Request
from fastapi.responses import JSONResponse

from ..crypto import generate_token_b64
from ..db import get_config_value, get_db, log_event, set_config_value

router = APIRouter()

CODE_DIGITS = 8
CODE_TTL = timedelta(minutes=10)
MAX_ATTEMPTS = 5


def _hash(code: str) -> str:
    return hashlib.sha256(code.encode()).hexdigest()


def _normalize(code: str) -> str:
    return "".join(ch for ch in code if ch.isdigit())


def create_pairing_code(db: sqlite3.Connection, now: datetime) -> tuple[str, datetime]:
    code = f"{secrets.randbelow(10**CODE_DIGITS):0{CODE_DIGITS}d}"
    expires = now + CODE_TTL
    set_config_value(db, "pairing_code_hash", _hash(code))
    set_config_value(db, "pairing_expires_utc", expires.isoformat())
    set_config_value(db, "pairing_attempts", 0)
    return code, expires


def clear_pairing_code(db: sqlite3.Connection) -> None:
    set_config_value(db, "pairing_code_hash", "")
    set_config_value(db, "pairing_expires_utc", "")
    set_config_value(db, "pairing_attempts", 0)


def active_pairing_expiry(db: sqlite3.Connection, now: datetime) -> datetime | None:
    if not get_config_value(db, "pairing_code_hash"):
        return None
    expires = datetime.fromisoformat(get_config_value(db, "pairing_expires_utc"))
    return expires if now < expires else None


def _server_url(db: sqlite3.Connection, request: Request) -> str:
    url = get_config_value(db, "watchdog_server_url")
    if url:
        return url
    # Fallback from the Host header; may be wrong behind a reverse proxy, hence the wizard asks for it.
    host = request.headers.get("host", "")
    scheme = request.headers.get("x-forwarded-proto", "http")
    return f"{scheme}://{host}"


@router.post("/v1/pair")
async def pair(request: Request, db: sqlite3.Connection = Depends(get_db)):
    body = await request.json()
    code = _normalize(str(body.get("code", ""))) if isinstance(body, dict) else ""
    now = datetime.now(timezone.utc)
    remote = request.client.host if request.client else "unknown"

    if active_pairing_expiry(db, now) is None:
        raise HTTPException(status_code=404, detail="no_active_pairing_code")

    if not hmac.compare_digest(_hash(code), get_config_value(db, "pairing_code_hash")):
        attempts = int(get_config_value(db, "pairing_attempts") or 0) + 1
        log_event(db, "pairing_failed", {"remote": remote, "attempts": attempts})
        if attempts >= MAX_ATTEMPTS:
            clear_pairing_code(db)
            log_event(db, "pairing_code_invalidated", {"reason": "too_many_attempts"})
        else:
            set_config_value(db, "pairing_attempts", attempts)
        left = max(0, MAX_ATTEMPTS - attempts)
        return JSONResponse(status_code=401, content={"detail": "invalid_code", "attempts_left": left})

    signing_key = generate_token_b64(32)
    set_config_value(db, "signing_key", signing_key)
    clear_pairing_code(db)
    # A (possibly different) PC starts fresh: no tamper comparison against the previous one.
    for key, empty in (("wd_instance_id", ""), ("wd_awake_ms", -1), ("wd_goodbye", "")):
        set_config_value(db, key, empty)
    log_event(db, "device_paired", {"remote": remote})
    return {"signing_key": signing_key, "server_url": _server_url(db, request)}
