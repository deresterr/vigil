"""Entry point FastAPI. Lifespan: init schema + bootstrap. Endpointy: /health + /v1/* + /admin/*."""

from __future__ import annotations

import logging
import sqlite3
from collections.abc import AsyncIterator
from contextlib import asynccontextmanager

from fastapi import Depends, FastAPI, HTTPException, Request, Response
from starlette.requests import ClientDisconnect

from .bootstrap import run_bootstrap
from .db import get_connection, get_db, init_database
from .endpoints.admin_api import router as admin_api_router
from .endpoints.admin_views import router as admin_views_router
from .endpoints.heartbeat import router as heartbeat_router
from .endpoints.journal import router as journal_router
from .endpoints.pairing import router as pairing_router
from .endpoints.pause import router as pause_router
from .endpoints.setup import router as setup_router
from .endpoints.totp import router as totp_router
from .signing import HmacSigningMiddleware

logging.basicConfig(
    level=logging.INFO,
    format="%(asctime)s [%(levelname)s] %(name)s: %(message)s",
)
logger = logging.getLogger("vigil")


class _AccessLogFilter(logging.Filter):
    """Wycisza success access logi dla wysokoczęstotliwościowych endpointów.

    Heartbeat co 5s + admin status polling co 5s + docker healthcheck co kilkanaście s
    zaśmiecają stdout. Errors (4xx/5xx) i wszystkie inne endpointy logujemy normalnie
    — diagnostyka się nie psuje.

    Uvicorn loguje access przez `self.access_logger.info('%s - "%s %s HTTP/%s" %d', ...)`,
    więc record.args = (client_addr, method, full_path, http_version, status_code).
    """

    _SILENT_PATHS = ("/v1/heartbeat", "/admin/api/status", "/health")

    def filter(self, record: logging.LogRecord) -> bool:
        args = record.args
        if not isinstance(args, tuple) or len(args) < 5:
            return True  # nieznany format — pass-through
        try:
            full_path = str(args[2])
            status_code = int(args[4])
        except (IndexError, ValueError, TypeError):
            return True
        if status_code >= 400:
            return True  # zawsze loguj błędy
        # full_path może mieć query string
        return not any(
            full_path == p or full_path.startswith(p + "?")
            for p in self._SILENT_PATHS
        )


logging.getLogger("uvicorn.access").addFilter(_AccessLogFilter())


@asynccontextmanager
async def lifespan(_: FastAPI) -> AsyncIterator[None]:
    """Startup: schema + bootstrap. Shutdown: nic do roboty (per-request connections)."""
    logger.info("Startup: inicjalizacja DB...")
    init_database()
    run_bootstrap()
    logger.info("Startup zakończony, listening...")
    yield
    logger.info("Shutdown.")


app = FastAPI(
    title="Vigil",
    version="0.4.0",
    lifespan=lifespan,
    docs_url=None,
    redoc_url=None,
    openapi_url=None,
)


@app.exception_handler(ClientDisconnect)
async def _handle_client_disconnect(request: Request, exc: ClientDisconnect) -> Response:
    """Klient zerwał TCP zanim serwer skończył czytać body — typowa sytuacja dla watchdoga
    nad WireGuardem (re-keying, drobne mrugnięcia sieci, restart usługi vigil_watchdog,
    30 s client-side timeout). Bez tego handler-a starlette puszcza 60-linijkowy traceback
    w log na każdy ucięty heartbeat.

    Response status 499 to nginx-owa konwencja „Client Closed Request" — klient i tak go
    nie zobaczy (jest już rozłączony), ale daje czysty sygnał monitoringowi że to NIE jest
    błąd serwera, tylko zerwane połączenie. Heartbeat zgubiony → watchdog wyśle następny za 5 s.
    """
    logger.info(
        "Client disconnected mid-request: %s %s", request.method, request.url.path
    )
    return Response(status_code=499)


@app.middleware("http")
async def setup_guard(request: Request, call_next):
    """Przekierowuje na /setup jeśli kreator nie został jeszcze ukończony.

    Przepuszcza bez sprawdzania: /setup/*, /health.
    Wszystkie inne ścieżki wymagają setup_complete == "1".
    """
    path = request.url.path
    if path.startswith("/setup") or path == "/health":
        return await call_next(request)

    with get_connection() as conn:
        from .db import get_config_value
        if get_config_value(conn, "setup_complete") != "1":
            from fastapi.responses import RedirectResponse as _RR
            return _RR(url="/setup", status_code=302)

    return await call_next(request)


# Middleware podpisujący odpowiedzi HMAC-SHA256.
# Dispatchowany dla każdego requestu, ale działa tylko dla tych które przeszły authenticate_request
# (sprawdza request.state.nonce / signing_key_bytes). Admin UI (/admin/*) nie wystawia
# request.state.nonce więc odpowiedzi admin nie są podpisywane — używają cookie session.
app.add_middleware(HmacSigningMiddleware)

# Kreator pierwszego uruchomienia — /setup, /setup/totp_qr, /setup/complete
app.include_router(setup_router)

# Watchdog endpoints (/v1/*) — signed requests (auth.py) + signed responses (signing.py)
app.include_router(heartbeat_router)
app.include_router(totp_router)
app.include_router(pause_router)

# Pairing with a one-time code (/v1/pair) — unauthenticated by design, see pairing.py
app.include_router(pairing_router)

# Admin UI (/admin, /admin/login, /admin/logout) — HTML + cookie session
app.include_router(admin_views_router)
# Admin JSON API (/admin/api/*) — cookie session required (dependency w routerze)
app.include_router(admin_api_router)
app.include_router(journal_router)


@app.get("/health")
def health(db: sqlite3.Connection = Depends(get_db)) -> dict[str, str]:
    """Healthcheck: aplikacja żyje + DB odpowiada na zapytanie. Bez auth, bez podpisu."""
    try:
        db.execute("SELECT 1").fetchone()
    except sqlite3.Error as e:
        logger.exception("Healthcheck DB failed")
        raise HTTPException(status_code=503, detail=f"db_error: {e}") from e
    with get_connection() as conn:
        from .db import get_config_value
        name = get_config_value(conn, "device_name") or "Vigil"
    return {"status": "ok", "name": name}


# Root redirect — bezpośrednio do /setup jeśli nie skonfigurowane, /admin jeśli OK.
@app.get("/")
def root_redirect():
    from fastapi.responses import RedirectResponse
    with get_connection() as _conn:
        from .db import get_config_value as _gcv
        setup_done = _gcv(_conn, "setup_complete") == "1"
    return RedirectResponse(url="/admin" if setup_done else "/setup", status_code=302)
