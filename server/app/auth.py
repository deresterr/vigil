"""Authentication of /v1/* requests from the watchdog (protocol v2).

Every request carries:
    X-Timestamp: unix time in ms
    X-Nonce:     random string (<= 256 chars), never reused
    X-Signature: base64(HMAC-SHA256(signing_key, canonical_request(...)))

canonical = METHOD "\\n" PATH "\\n" TIMESTAMP "\\n" NONCE "\\n" hex(sha256(body))

PATH is the API route as the app sees it ("/v1/heartbeat"), without any reverse-proxy prefix.
Responses are signed by HmacSigningMiddleware (signing.py) over nonce + body.

Which device (D7): every paired PC has its own key (`devices.signing_key`, issued by pairing). The
request carries no device id; the server tries the keys of all devices and the one whose HMAC matches
is the sender. A handful of HMACs per request, and the watchdog protocol stays unchanged.

Threat model: friction, not protection. The key lives in signing.dat (DPAPI LocalMachine, SYSTEM-only
ACL), which a non-elevated process cannot read but an admin can. What it stops: forging or replaying
requests without the key, e.g. from another device on the LAN or from the child's non-elevated session.
A key only speaks for its own device and child.
"""

from __future__ import annotations

import base64
import hashlib
import hmac
import sqlite3
from dataclasses import dataclass
from datetime import datetime, timedelta, timezone

from fastapi import Depends, HTTPException, Request

from .db import get_config_value, get_db

_MAX_FUTURE_SKEW_MS = 5000


@dataclass(frozen=True)
class Device:
    """The authenticated sender of a /v1 request."""

    id: int
    child_id: int
    name: str


def decode_signing_key(b64: str) -> bytes:
    """Decode base64 urlsafe without padding (format produced by generate_token_b64())."""
    return base64.urlsafe_b64decode(b64 + "=" * (-len(b64) % 4))


def canonical_request(method: str, path: str, timestamp: str, nonce: str, body: bytes) -> bytes:
    return "\n".join((method.upper(), path, timestamp, nonce, hashlib.sha256(body).hexdigest())).encode()


def sign(key: bytes, message: bytes) -> bytes:
    return hmac.new(key, message, hashlib.sha256).digest()


def _reject(detail: str) -> HTTPException:
    return HTTPException(status_code=401, detail=detail)


async def authenticate_request(request: Request, db: sqlite3.Connection = Depends(get_db)) -> Device:
    """Dependency for every /v1/* endpoint except /v1/pair; returns the sending device.

    Order matters: everything is checked before the nonce is stored, so a rejected request never
    burns a nonce. The nonce is committed at once: a request that fails later has still used it.

    The connection is get_db()'s, shared with the endpoint (FastAPI resolves a dependency once per
    request). It used to be one of its own, outside get_db's lock (#123): while a panel request held the
    lock with its write not yet committed, this INSERT waited out SQLite's 30 s busy timeout inside the event
    loop, so the panel request could never commit — "database is locked", and the server stood still.

    Side effect: request.state.{signing_key_bytes, nonce} for the response-signing middleware.
    """
    ts_header = request.headers.get("X-Timestamp")
    nonce = request.headers.get("X-Nonce")
    sig_header = request.headers.get("X-Signature")
    if not ts_header or not nonce or not sig_header:
        raise _reject("missing_auth_headers")
    if len(nonce) > 256:
        raise _reject("nonce_too_long")
    try:
        ts_ms = int(ts_header)
        provided = base64.b64decode(sig_header, validate=True)
    except ValueError as e:
        raise _reject("malformed_auth_headers") from e

    body = await request.body()  # cached by Starlette; the endpoint reads it again

    message = canonical_request(request.method, request.url.path, ts_header, nonce, body)
    device: Device | None = None
    key = b""
    # Every key is tried (no early exit), so timing does not tell how many devices there are
    # or which one matched.
    for row in db.execute("SELECT id, child_id, name, signing_key FROM devices").fetchall():
        candidate = decode_signing_key(row["signing_key"])
        if hmac.compare_digest(sign(candidate, message), provided) and device is None:
            device, key = Device(row["id"], row["child_id"], row["name"]), candidate
    if device is None:
        raise _reject("invalid_signature")

    now = datetime.now(timezone.utc)
    diff_ms = int(now.timestamp() * 1000) - ts_ms
    max_age_ms = int(get_config_value(db, "max_request_age_seconds") or 60) * 1000
    if diff_ms > max_age_ms:
        raise _reject("timestamp_too_old")
    if diff_ms < -_MAX_FUTURE_SKEW_MS:
        raise _reject("timestamp_in_future")

    try:
        db.execute("INSERT INTO used_nonces(nonce, ts_utc) VALUES (?, ?)", (nonce, now.isoformat()))
    except sqlite3.IntegrityError as e:
        raise _reject("duplicate_nonce") from e

    # Nonces older than the accepted request age can never be replayed anyway.
    keep_s = int(get_config_value(db, "nonce_cache_seconds") or 120)
    db.execute("DELETE FROM used_nonces WHERE ts_utc < ?", ((now - timedelta(seconds=keep_s)).isoformat(),))
    db.commit()

    request.state.signing_key_bytes = key
    request.state.nonce = nonce
    return device
