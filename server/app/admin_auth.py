"""Panel authentication: password → session cookie (itsdangerous, 24 h, HttpOnly, SameSite=Strict).

The panel uses a cookie, the watchdog signed requests. SameSite=Strict against CSRF, HttpOnly against
script access, the itsdangerous signature (key `admin_session_key`) against forged cookies.

Revocation: the cookie carries `admin_session_generation`; a logout or a password change bumps it, which
ends every session issued before. The parent often logs in on the child's PC (one Windows account, D12),
where the child can copy the browser's cookies: after a logout such a copy is worthless.

The parent's password is an argon2id hash in `config.parent_password_hash`.
"""

from __future__ import annotations

import sqlite3
import time

from fastapi import Depends, HTTPException, Request
from itsdangerous import BadSignature, SignatureExpired, URLSafeTimedSerializer

from .crypto import verify_password as _crypto_verify_password
from .db import get_config_value, get_db, set_config_value

# Cookie name + properties
SESSION_COOKIE = "vigil_session"
SESSION_MAX_AGE_SECONDS = 24 * 60 * 60  # 24h
_ITSDANGEROUS_SALT = "vigil-admin-session-v1"


def _serializer(db: sqlite3.Connection) -> URLSafeTimedSerializer:
    """The signer with the key from the database (read per request: cheap)."""
    secret = get_config_value(db, "admin_session_key")
    if not secret:
        raise RuntimeError(
            "admin_session_key missing from config: the startup bootstrap did not run"
        )
    return URLSafeTimedSerializer(secret, salt=_ITSDANGEROUS_SALT)


def verify_admin_password(db: sqlite3.Connection, password: str) -> bool:
    """The password against the stored hash. argon2id costs ~50–200 ms of CPU; repeated failures are
    throttled per IP (record_login_failure)."""
    stored_hash = get_config_value(db, "parent_password_hash")
    if not stored_hash:
        return False
    return _crypto_verify_password(password, stored_hash)


def _generation(db: sqlite3.Connection) -> int:
    return int(get_config_value(db, "admin_session_generation") or 0)


def issue_session_cookie(db: sqlite3.Connection) -> str:
    """A signed session token (the cookie value) of the current generation."""
    return _serializer(db).dumps({"sub": "parent", "gen": _generation(db)})


def revoke_sessions(db: sqlite3.Connection) -> None:
    """Every session cookie issued so far stops working."""
    set_config_value(db, "admin_session_generation", _generation(db) + 1)


def session_payload(db: sqlite3.Connection, cookie: str | None) -> dict:
    """The payload of a valid, unexpired, unrevoked session cookie; HTTPException 401 otherwise."""
    if not cookie:
        raise HTTPException(status_code=401, detail="no_session")
    try:
        payload = _serializer(db).loads(cookie, max_age=SESSION_MAX_AGE_SECONDS)
    except SignatureExpired as e:
        raise HTTPException(status_code=401, detail="session_expired") from e
    except BadSignature as e:
        raise HTTPException(status_code=401, detail="invalid_session") from e
    if not isinstance(payload, dict) or payload.get("gen") != _generation(db):
        raise HTTPException(status_code=401, detail="session_revoked")
    return payload


def set_session_cookie(response, cookie_value: str) -> None:
    """The session cookie. Not Secure: the server is meant for plain HTTP on the home network or a VPN."""
    response.set_cookie(
        key=SESSION_COOKIE,
        value=cookie_value,
        max_age=SESSION_MAX_AGE_SECONDS,
        httponly=True,
        secure=False,
        samesite="strict",
        path="/admin",
    )


def clear_session_cookie(response) -> None:
    response.delete_cookie(key=SESSION_COOKIE, path="/admin")


async def require_admin_session(
    request: Request,
    db: sqlite3.Connection = Depends(get_db),
) -> dict:
    """Dependency of /admin/api/*: 401 without a valid, unexpired, unrevoked session cookie.

    Returns the cookie's payload ({"sub": "parent", "gen": N}).
    """
    return session_payload(db, request.cookies.get(SESSION_COOKIE))


# === Login throttling ===
# Per client IP, in memory: single uvicorn worker (see CLAUDE.md) and a restart is not something the
# child can trigger, so no persistence is needed. Per IP so a child hammering the login from their PC
# does not lock the parent out on another device. Behind a reverse proxy all clients share one IP,
# which degrades to a global limit — still safe, just stricter.
_FAILURES_PER_LOCKOUT = 5
_LOCKOUT_STEPS_SECONDS = (60, 300, 900)
_FORGET_AFTER_SECONDS = 86400

_login_failures: dict[str, tuple[int, float, float]] = {}  # ip -> (count, first_failure, locked_until)


def login_lockout_remaining(ip: str, now: float | None = None) -> int:
    """Seconds this IP must still wait before a login attempt is evaluated; 0 = allowed."""
    now = time.monotonic() if now is None else now
    entry = _login_failures.get(ip)
    if not entry:
        return 0
    count, first, locked_until = entry
    if now - first > _FORGET_AFTER_SECONDS and now >= locked_until:
        del _login_failures[ip]
        return 0
    return max(0, int(locked_until - now + 0.999))


def record_login_failure(ip: str, now: float | None = None) -> int:
    """Count a failed login; returns the lockout length in seconds if one starts now, else 0."""
    now = time.monotonic() if now is None else now
    count, first, _ = _login_failures.get(ip, (0, now, 0.0))
    count += 1
    lockout = 0
    if count % _FAILURES_PER_LOCKOUT == 0:
        idx = min(count // _FAILURES_PER_LOCKOUT, len(_LOCKOUT_STEPS_SECONDS)) - 1
        lockout = _LOCKOUT_STEPS_SECONDS[idx]
    _login_failures[ip] = (count, first, now + lockout)
    return lockout


def reset_login_failures(ip: str) -> None:
    _login_failures.pop(ip, None)
