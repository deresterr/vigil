"""Auth dla admin UI: hasło → cookie session (itsdangerous, 24h, HttpOnly, SameSite=Strict).

Zamiennie do bearer/HMAC z watchdoga — admin UI używa standardowych cookie. SameSite=Strict
chroni przed CSRF, HttpOnly blokuje dostęp z JS (XSS mitigation), itsdangerous podpisuje
cookie tak że nikt nie sforge'uje (klucz `admin_session_key` z DB).

Hasło rodzica zapisane jako argon2id hash w `config.parent_password_hash`.
"""

from __future__ import annotations

import sqlite3

from fastapi import Depends, HTTPException, Request
from itsdangerous import BadSignature, SignatureExpired, URLSafeTimedSerializer

from .crypto import verify_password as _crypto_verify_password
from .db import get_config_value, get_db

# Cookie name + properties
SESSION_COOKIE = "vigil_session"
SESSION_MAX_AGE_SECONDS = 24 * 60 * 60  # 24h
_ITSDANGEROUS_SALT = "vigil-admin-session-v1"


def _serializer(db: sqlite3.Connection) -> URLSafeTimedSerializer:
    """Buduje serializer z secret z DB. Wywoływane przy każdym requestcie — tani lookup."""
    secret = get_config_value(db, "admin_session_key")
    if not secret:
        raise RuntimeError(
            "Brakuje admin_session_key w config — bootstrap nie został uruchomiony lub baza jest stara. "
            "Wygeneruj klucz ręcznie albo zresetuj DB."
        )
    return URLSafeTimedSerializer(secret, salt=_ITSDANGEROUS_SALT)


def verify_admin_password(db: sqlite3.Connection, password: str) -> bool:
    """Weryfikuje hasło względem hashu z config. Argon2id sam jest CPU-bound (~50-200ms),
    więc nie potrzebujemy dodatkowego sleep przeciwko brute force.
    """
    stored_hash = get_config_value(db, "parent_password_hash")
    if not stored_hash:
        return False
    return _crypto_verify_password(password, stored_hash)


def issue_session_cookie(db: sqlite3.Connection) -> str:
    """Generuje podpisany token (cookie value) dla zalogowanego admina."""
    return _serializer(db).dumps({"sub": "parent"})


def set_session_cookie(response, cookie_value: str) -> None:
    """Ustawia cookie z bezpiecznymi flagami. Secure=False bo serwer LAN-only HTTP."""
    response.set_cookie(
        key=SESSION_COOKIE,
        value=cookie_value,
        max_age=SESSION_MAX_AGE_SECONDS,
        httponly=True,
        secure=False,  # LAN HTTP-only deployment
        samesite="strict",
        path="/admin",
    )


def clear_session_cookie(response) -> None:
    response.delete_cookie(key=SESSION_COOKIE, path="/admin")


async def require_admin_session(
    request: Request,
    db: sqlite3.Connection = Depends(get_db),
) -> dict:
    """Dependency dla endpointów /admin/api/*. 401 jeśli brak/zły/expired cookie.

    Returns: payload z cookie ({"sub": "parent"}).
    """
    cookie = request.cookies.get(SESSION_COOKIE)
    if not cookie:
        raise HTTPException(status_code=401, detail="no_session")

    serializer = _serializer(db)
    try:
        payload = serializer.loads(cookie, max_age=SESSION_MAX_AGE_SECONDS)
    except SignatureExpired as e:
        raise HTTPException(status_code=401, detail="session_expired") from e
    except BadSignature as e:
        raise HTTPException(status_code=401, detail="invalid_session") from e

    return payload
