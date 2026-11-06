"""Walidacja request od watchdoga: bearer + nonce + timestamp.

Każdy fail → 401. Dependency zapisuje state w `request.state` dla HMAC signing middleware.
Single-tenant: bearer_token i signing_key trzymane jako pojedyncze klucze w `config` table.
"""

from __future__ import annotations

import base64
import hmac
import sqlite3
import time
from datetime import datetime, timedelta, timezone

from fastapi import HTTPException, Request

from .db import get_connection


def _decode_signing_key(b64: str) -> bytes:
    """Decode base64 urlsafe (bez paddingu) — format z generate_token_b64()."""
    padding = "=" * (-len(b64) % 4)
    return base64.urlsafe_b64decode(b64 + padding)


# ── In-memory cache konfiguracji auth ────────────────────────────────────────
# bearer_token, signing_key*, max_request_age_seconds, nonce_cache_seconds
# nigdy nie zmieniają się w runtime — czytamy raz i cachujemy.
# invalidate_auth_cache() wołane przez admin_api.regen_tokens po commicie nowych tokenów.
#
# signing_key_bytes dekodowany przy załadowaniu cache (NIE wewnątrz transakcji z nonce INSERT)
# — żeby ewentualny ValueError nie spowodował rollback nonce'a.
_auth_cfg: dict | None = None
_auth_cfg_ts: float = 0.0
_AUTH_CFG_TTL = 300.0   # 5 min safety net


def invalidate_auth_cache() -> None:
    """Czyść cache — wołane przez admin_api.regen_tokens PO commicie nowych tokenów."""
    global _auth_cfg
    _auth_cfg = None


def _get_auth_cfg(db: sqlite3.Connection) -> dict:
    """Zwraca konfigurację auth z cache lub odczytuje z DB."""
    global _auth_cfg, _auth_cfg_ts
    now = time.monotonic()
    if _auth_cfg is not None and now - _auth_cfg_ts < _AUTH_CFG_TTL:
        return _auth_cfg

    rows = dict(
        db.execute(
            "SELECT key, value FROM config WHERE key IN "
            "('bearer_token', 'signing_key', 'max_request_age_seconds', 'nonce_cache_seconds')"
        ).fetchall()
    )
    signing_key_b64 = rows.get("signing_key", "")
    # Dekoduj signing_key TUTAJ (poza transakcją INSERT nonce) — jeśli b64 jest
    # nieprawidłowy, wyjątek pada podczas cache load, nie po INSERT.
    try:
        signing_key_bytes = _decode_signing_key(signing_key_b64) if signing_key_b64 else None
    except Exception:
        signing_key_bytes = None

    _auth_cfg = {
        "bearer_token":        rows.get("bearer_token", ""),
        "signing_key_b64":     signing_key_b64,
        "signing_key_bytes":   signing_key_bytes,
        "max_age_ms":          int(rows.get("max_request_age_seconds", "60")) * 1000,
        "nonce_cache_seconds": int(rows.get("nonce_cache_seconds", "120")),
    }
    _auth_cfg_ts = now
    return _auth_cfg


# Throttle nonce cleanup — nie co request, tylko co N requestów.
_auth_counter = 0
_NONCE_CLEANUP_INTERVAL = 50


def _maybe_cleanup_nonces(conn: sqlite3.Connection, cache_seconds: int) -> None:
    global _auth_counter
    _auth_counter += 1
    if _auth_counter % _NONCE_CLEANUP_INTERVAL != 0:
        return
    cutoff = datetime.now(timezone.utc) - timedelta(seconds=cache_seconds)
    conn.execute("DELETE FROM used_nonces WHERE ts_utc < ?", (cutoff.isoformat(),))


async def authenticate_request(request: Request) -> None:
    """Dependency dla wszystkich endpointów `/v1/*`. Single-tenant — brak client_id.

    Używa własnego krótkotrwałego connection + in-memory cache konfiguracji.
    Write lock (nonce INSERT) trzymany tylko ~1ms.

    Side effect: ustawia request.state.{signing_key_bytes, nonce} dla middleware.
    """
    # 1. Fail-fast bez otwierania DB
    auth_header = request.headers.get("Authorization", "")
    if not auth_header.startswith("Bearer "):
        raise HTTPException(status_code=401, detail="missing_bearer")
    bearer = auth_header[len("Bearer "):]

    ts_header = request.headers.get("X-Timestamp")
    if not ts_header:
        raise HTTPException(status_code=401, detail="missing_timestamp")

    nonce = request.headers.get("X-Nonce")
    if not nonce:
        raise HTTPException(status_code=401, detail="missing_nonce")
    if len(nonce) > 256:
        raise HTTPException(status_code=401, detail="nonce_too_long")

    # 2. Krótkotrwały connection — commit i close zaraz po auth
    with get_connection() as db:
        cfg = _get_auth_cfg(db)  # cache miss → 1 SELECT; hit → 0 DB ops

        # Bearer token (constant-time compare)
        if not cfg["bearer_token"] or not hmac.compare_digest(bearer, cfg["bearer_token"]):
            raise HTTPException(status_code=401, detail="invalid_bearer")

        # signing_key_bytes sprawdzamy przed INSERT — błąd konfiguracji, nie auth failure
        if not cfg["signing_key_bytes"]:
            raise HTTPException(status_code=500, detail="missing_signing_key")

        # Timestamp
        try:
            ts_ms = int(ts_header)
        except ValueError as e:
            raise HTTPException(status_code=401, detail="invalid_timestamp") from e

        now_ms = int(datetime.now(timezone.utc).timestamp() * 1000)
        diff_ms = now_ms - ts_ms
        if diff_ms > cfg["max_age_ms"]:
            raise HTTPException(status_code=401, detail="timestamp_too_old")
        if diff_ms < -5000:
            raise HTTPException(status_code=401, detail="timestamp_in_future")

        # Nonce INSERT — jedyna write operacja. Wszystkie HTTPException powyżej
        # są rzucane PRZED tym INSERT-em, więc rollback jest zawsze semantycznie poprawny.
        try:
            db.execute(
                "INSERT INTO used_nonces(nonce, ts_utc) VALUES (?, ?)",
                (nonce, datetime.now(timezone.utc).isoformat()),
            )
        except sqlite3.IntegrityError as e:
            raise HTTPException(status_code=401, detail="duplicate_nonce") from e

        # Nonce cleanup — throttled. Opakowane try/except: ewentualny błąd DELETE
        # NIE może rollbackować INSERT — cleanup jest opcjonalny housekeeping.
        try:
            _maybe_cleanup_nonces(db, cfg["nonce_cache_seconds"])
        except Exception:
            pass  # cleanup to housekeeping — nie przerywaj autentykacji

        # signing_key_bytes już zdekodowane w _get_auth_cfg (przed INSERT)
        signing_key_bytes = cfg["signing_key_bytes"]

    # Connection zamknięty — write lock zwolniony ZANIM endpoint zacznie
    request.state.signing_key_bytes = signing_key_bytes
    request.state.nonce = nonce
