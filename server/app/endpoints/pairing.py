"""Pairing a child PC with the server using a one-time code.

Flow: the parent generates a code in the panel (POST /admin/api/pairing_code), the installer on the
child PC sends it to POST /v1/pair and receives a freshly generated signing key. The server address is
the one the installer reached it at.

The code is made for one target (D7): a new device of a given child (name entered in the panel), or
an existing device being re-paired (reinstall). Re-pairing replaces that device's key, so the old
install stops authenticating, and resets its tamper baseline.

- 8 digits, valid 10 minutes, single use, 5 wrong attempts invalidate it (guessing odds ~5e-8).
- One code at a time: generating a new one invalidates the previous one.
- Only a hash of the code is stored.

The code keeps other devices on the network from claiming a key. The response travels over plain HTTP like
every other request; put a reverse proxy with TLS in front if that matters in your network.
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
from ..models import PairRequest

router = APIRouter()

CODE_DIGITS = 8
CODE_TTL = timedelta(minutes=10)
MAX_ATTEMPTS = 5


def _hash(code: str) -> str:
    return hashlib.sha256(code.encode()).hexdigest()


def _normalize(code: str) -> str:
    return "".join(ch for ch in code if ch.isdigit())


def create_pairing_code(db: sqlite3.Connection, now: datetime, *, child_id: int | None = None,
                        device_name: str | None = None, device_id: int | None = None) -> tuple[str, datetime]:
    """A code for a new device of `child_id` named `device_name`, or for re-pairing `device_id`."""
    code = f"{secrets.randbelow(10**CODE_DIGITS):0{CODE_DIGITS}d}"
    expires = now + CODE_TTL
    set_config_value(db, "pairing_code_hash", _hash(code))
    set_config_value(db, "pairing_expires_utc", expires.isoformat())
    set_config_value(db, "pairing_attempts", 0)
    set_config_value(db, "pairing_child_id", child_id or "")
    set_config_value(db, "pairing_device_id", device_id or "")
    set_config_value(db, "pairing_device_name", device_name or "")
    return code, expires


def clear_pairing_code(db: sqlite3.Connection) -> None:
    for key in ("pairing_code_hash", "pairing_expires_utc", "pairing_child_id", "pairing_device_id",
                "pairing_device_name"):
        set_config_value(db, key, "")
    set_config_value(db, "pairing_attempts", 0)


def active_pairing_expiry(db: sqlite3.Connection, now: datetime) -> datetime | None:
    if not get_config_value(db, "pairing_code_hash"):
        return None
    expires = datetime.fromisoformat(get_config_value(db, "pairing_expires_utc"))
    return expires if now < expires else None


@router.post("/v1/pair")
async def pair(body: PairRequest, request: Request, db: sqlite3.Connection = Depends(get_db)):
    code = _normalize(body.code)
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
    device_id = int(get_config_value(db, "pairing_device_id") or 0)
    if device_id:
        # Re-pair: new key, and the reinstalled watchdog starts fresh (no tamper comparison).
        db.execute("UPDATE devices SET signing_key = ?, paired_utc = ?, wd_instance_id = '', wd_awake_ms = -1, "
                   "wd_goodbye = '' WHERE id = ?", (signing_key, now.isoformat(), device_id))
        child_id = db.execute("SELECT child_id FROM devices WHERE id = ?", (device_id,)).fetchone()["child_id"]
    else:
        child_id = int(get_config_value(db, "pairing_child_id"))
        device_id = db.execute(
            "INSERT INTO devices(child_id, name, signing_key, paired_utc) VALUES (?, ?, ?, ?)",
            (child_id, get_config_value(db, "pairing_device_name"), signing_key, now.isoformat()),
        ).lastrowid
    clear_pairing_code(db)
    log_event(db, "device_paired", {"remote": remote}, child_id=child_id, device_id=device_id)
    return {"signing_key": signing_key}
