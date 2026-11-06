"""Endpoint zaciągania credentials przez Install.cmd/vigil_install.ps1 (/v1/claim_credentials).

Aktywny tylko gdy init_open == "1". Brak autoryzacji — celowo, dla home LAN.
Install.cmd wywołuje go raz po podaniu adresu serwera zamiast ręcznego przepisywania
bearer_token i signing_key z admin UI.
"""

from __future__ import annotations

import sqlite3

from fastapi import APIRouter, Depends, HTTPException, Request

from ..db import get_config_value, get_db, log_event

router = APIRouter()


@router.get("/v1/claim_credentials")
async def claim_credentials(
    request: Request,
    db: sqlite3.Connection = Depends(get_db),
) -> dict:
    if get_config_value(db, "init_open") != "1":
        raise HTTPException(status_code=404, detail="init_mode_not_active")

    bearer_token = get_config_value(db, "bearer_token")
    signing_key  = get_config_value(db, "signing_key")
    server_url   = get_config_value(db, "watchdog_server_url")

    if not bearer_token or not signing_key:
        raise HTTPException(status_code=503, detail="credentials_not_ready")

    # Fallback: jeśli admin nie ustawił watchdog_server_url,
    # konstruujemy z Host headera requestu. Może być niepoprawny przy reverse proxy /
    # DNS rewriting — dlatego wizard wymaga jawnego ustawienia.
    if not server_url:
        host = request.headers.get("host", "")
        scheme = request.headers.get("x-forwarded-proto", "http")
        server_url = f"{scheme}://{host}"

    log_event(db, "credentials_claimed",
              {"remote": request.client.host if request.client else "unknown"})

    return {
        "bearer_token": bearer_token,
        "signing_key":  signing_key,
        "server_url":   server_url,
    }
