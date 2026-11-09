"""FastAPI entry point. Lifespan: schema + bootstrap. Routes: /health, /setup, /v1/* (watchdog), /admin/*."""

from __future__ import annotations

import logging
import sqlite3
from collections.abc import AsyncIterator
from contextlib import asynccontextmanager

from fastapi import Depends, FastAPI, HTTPException, Request, Response
from fastapi.responses import RedirectResponse
from starlette.requests import ClientDisconnect

from .bootstrap import run_bootstrap
from .db import get_config_value, get_connection, get_db, init_database
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
    """Drops successful access-log lines of the frequent endpoints.

    Heartbeats and panel status polling every 5 s and the Docker healthcheck would flood stdout. Errors
    (4xx/5xx) and every other endpoint are logged as usual.

    Uvicorn logs access as `self.access_logger.info('%s - "%s %s HTTP/%s" %d', ...)`, so
    record.args = (client_addr, method, full_path, http_version, status_code).
    """

    _SILENT_PATHS = ("/v1/heartbeat", "/admin/api/status", "/health")

    def filter(self, record: logging.LogRecord) -> bool:
        args = record.args
        if not isinstance(args, tuple) or len(args) < 5:
            return True  # unknown format: keep it
        try:
            full_path = str(args[2])
            status_code = int(args[4])
        except (IndexError, ValueError, TypeError):
            return True
        if status_code >= 400:
            return True
        # full_path may carry a query string
        return not any(
            full_path == p or full_path.startswith(p + "?")
            for p in self._SILENT_PATHS
        )


logging.getLogger("uvicorn.access").addFilter(_AccessLogFilter())


@asynccontextmanager
async def lifespan(_: FastAPI) -> AsyncIterator[None]:
    """Startup: schema + bootstrap. Nothing to do at shutdown (connections are per request)."""
    logger.info("Startup: database...")
    init_database()
    run_bootstrap()
    logger.info("Startup complete")
    yield
    logger.info("Shutdown.")


app = FastAPI(
    title="Vigil",
    lifespan=lifespan,
    docs_url=None,
    redoc_url=None,
    openapi_url=None,
)


@app.exception_handler(ClientDisconnect)
async def _handle_client_disconnect(request: Request, exc: ClientDisconnect) -> Response:
    """The client dropped the connection before the body was read: common for a watchdog over a VPN
    (re-keying, short network blips, a service restart, its own request timeout). Without this handler
    Starlette logs a long traceback for every cut-off heartbeat.

    499 is nginx's "Client Closed Request": the client never sees it, but the log shows a dropped connection,
    not a server error. The watchdog sends the next heartbeat 5 s later.
    """
    logger.info(
        "Client disconnected mid-request: %s %s", request.method, request.url.path
    )
    return Response(status_code=499)


@app.middleware("http")
async def setup_guard(request: Request, call_next):
    """Until the setup wizard is complete everything but /setup/* and /health redirects to /setup."""
    path = request.url.path
    if path.startswith("/setup") or path == "/health":
        return await call_next(request)

    with get_connection() as conn:
        if get_config_value(conn, "setup_complete") != "1":
            return RedirectResponse(url="/setup", status_code=302)

    return await call_next(request)


# Signs the responses of authenticated /v1/* requests (signing.py); the panel uses a cookie session and
# its responses are not signed.
app.add_middleware(HmacSigningMiddleware)

# First-run wizard: /setup, /setup/totp_qr, /setup/complete
app.include_router(setup_router)

# Watchdog endpoints (/v1/*): signed requests (auth.py) and signed responses (signing.py)
app.include_router(heartbeat_router)
app.include_router(totp_router)
app.include_router(pause_router)

# Pairing with a one-time code (/v1/pair): unauthenticated by design, see pairing.py
app.include_router(pairing_router)

# Panel pages (/admin, /admin/login, /admin/logout): HTML + cookie session
app.include_router(admin_views_router)
# Panel JSON API (/admin/api/*): cookie session required (router dependency)
app.include_router(admin_api_router)
app.include_router(journal_router)


@app.get("/health")
def health(db: sqlite3.Connection = Depends(get_db)) -> dict[str, str]:
    """The app runs and the database answers. No authentication, no signature, no error details."""
    try:
        db.execute("SELECT 1").fetchone()
    except sqlite3.Error as e:
        logger.exception("Healthcheck DB failed")
        raise HTTPException(status_code=503, detail="db_error") from e
    return {"status": "ok"}


@app.get("/")
def root_redirect():
    """/setup until the wizard is complete, then /admin."""
    with get_connection() as conn:
        setup_done = get_config_value(conn, "setup_complete") == "1"
    return RedirectResponse(url="/admin" if setup_done else "/setup", status_code=302)
