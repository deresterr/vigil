"""HTML routes dla admin UI: /admin (panel), /admin/login (form), /admin/logout.

Frontend (admin.html) jest SPA z vanilla JS — woła /admin/api/* przez fetch.
"""

from __future__ import annotations

import sqlite3
from pathlib import Path

from fastapi import APIRouter, Depends, Form, Request
from fastapi.responses import HTMLResponse, RedirectResponse, Response

from ..admin_auth import (
    clear_session_cookie,
    issue_session_cookie,
    set_session_cookie,
    verify_admin_password,
)
from ..db import get_db, log_event

router = APIRouter()

# Static HTML files trzymamy w app/static/ — embedded CSS i JS w jednym pliku.
_STATIC_DIR = Path(__file__).parent.parent / "static"


def _read_static(name: str) -> str:
    return (_STATIC_DIR / name).read_text(encoding="utf-8")


@router.get("/admin", response_class=HTMLResponse)
async def admin_panel(request: Request) -> Response:
    """Główny panel. Cookie session sprawdzamy po stronie JS — jeśli /admin/api/status
    zwróci 401, frontend przekierowuje na /admin/login. Sam /admin nie waliduje cookie
    żeby uniknąć podwójnego renderu.
    """
    return HTMLResponse(_read_static("admin.html"))


@router.get("/admin/login", response_class=HTMLResponse)
async def admin_login_form(request: Request) -> Response:
    return HTMLResponse(_read_static("login.html"))


@router.post("/admin/login")
async def admin_login_submit(
    request: Request,
    password: str = Form(...),
    db: sqlite3.Connection = Depends(get_db),
) -> Response:
    """Walidacja hasła. Sukces → set cookie + redirect na /admin. Błąd → redirect z error param."""
    if not verify_admin_password(db, password):
        log_event(db, "admin_login_failed")
        # Redirect z query param error=1 — login.html pokaże błąd
        return RedirectResponse(url="/admin/login?error=1", status_code=303)

    log_event(db, "admin_login_success")
    cookie_value = issue_session_cookie(db)
    response = RedirectResponse(url="/admin", status_code=303)
    set_session_cookie(response, cookie_value)
    return response


@router.post("/admin/logout")
async def admin_logout(request: Request) -> Response:
    """Czyści cookie i przekierowuje na login. Nie wymaga ważnej sesji — możesz wylogować
    się z expired session.
    """
    response = RedirectResponse(url="/admin/login", status_code=303)
    clear_session_cookie(response)
    return response
