"""FastAPI entry point. Lifespan: schema + bootstrap. Routes: /health, /setup, /v1/* (watchdog), /admin/*."""

from __future__ import annotations

import logging
import os
import sqlite3
from collections.abc import AsyncIterator
from contextlib import asynccontextmanager

from fastapi import Depends, FastAPI, HTTPException, Request, Response
from fastapi.responses import PlainTextResponse, RedirectResponse
from starlette.requests import ClientDisconnect

from . import db as db_module
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
from .logs import VERSION, setup_logging
from .signing import HmacSigningMiddleware

setup_logging()
logger = logging.getLogger("vigil")


@asynccontextmanager
async def lifespan(_: FastAPI) -> AsyncIterator[None]:
    """Startup: schema + bootstrap, then one log line for the whole start (logs.py). Nothing to do at shutdown
    (connections are per request)."""
    schema = init_database()
    first_run = run_bootstrap()
    port = os.environ.get("LISTEN_PORT")  # set by the Dockerfile; unknown when uvicorn is run by hand
    logger.info("Vigil %s started%s · %s (%sschema %d)", VERSION, f" · port {port}" if port else "",
                db_module.DATABASE_PATH, "new, " if first_run else "", schema)
    with get_connection() as conn:
        if get_config_value(conn, "setup_complete") != "1":
            logger.warning("Setup not completed: open /setup to finish it")
    yield
    logger.info("Vigil stopped")


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

    499 is nginx's "Client Closed Request": the client never sees it. Debug level: network noise, not a server
    error (an offline PC shows in the journal). The watchdog sends the next heartbeat 5 s later.
    """
    logger.debug("Client disconnected mid-request: %s %s", request.method, request.url.path)
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


# Added last = the outermost middleware, so it also sees errors of the ones above.
@app.middleware("http")
async def log_unhandled_errors(request: Request, call_next):
    """An unhandled exception: one ERROR line naming the request, with the traceback (logs.py). Without this,
    Starlette re-raises it and uvicorn logs a bare "Exception in ASGI application". The response is the same
    plain 500 Starlette sends (unsigned, so a watchdog treats it as a failed request)."""
    try:
        return await call_next(request)
    except Exception as exc:
        # BaseHTTPMiddleware re-raises the error "from" the task group that caught it, a group holding the same
        # error: the traceback would be printed twice.
        cause = exc.__cause__
        if isinstance(cause, BaseExceptionGroup) and exc in cause.exceptions:
            exc.__cause__ = None
            exc.__suppress_context__ = True
        logger.exception("Unhandled error in %s %s", request.method, request.url.path)
        return PlainTextResponse("Internal Server Error", status_code=500)

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


# Docker's healthcheck asks every 30 s: a database failure is logged once, then its recovery.
_health_failing = False


@app.get("/health")
def health(db: sqlite3.Connection = Depends(get_db)) -> dict[str, str]:
    """The app runs and the database answers. No authentication, no signature, no error details."""
    global _health_failing
    try:
        db.execute("SELECT 1").fetchone()
    except sqlite3.Error as e:
        if not _health_failing:
            _health_failing = True
            logger.exception("Health check: database not responding")
        raise HTTPException(status_code=503, detail="db_error") from e
    if _health_failing:
        _health_failing = False
        logger.info("Health check: database OK again")
    return {"status": "ok"}


@app.get("/")
def root_redirect():
    """/setup until the wizard is complete, then /admin."""
    with get_connection() as conn:
        setup_done = get_config_value(conn, "setup_complete") == "1"
    return RedirectResponse(url="/admin" if setup_done else "/setup", status_code=302)
