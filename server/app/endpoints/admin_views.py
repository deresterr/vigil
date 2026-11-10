"""Panel pages: /admin (the panel), /admin/login (form), /admin/logout.

admin.html is a single page in plain JS that calls /admin/api/* with fetch.
"""

from __future__ import annotations

import sqlite3

from fastapi import APIRouter, Depends, Form, HTTPException, Request
from fastapi.responses import HTMLResponse, RedirectResponse, Response

from ..admin_auth import (
    SESSION_COOKIE,
    clear_session_cookie,
    issue_session_cookie,
    login_lockout_remaining,
    record_login_failure,
    reset_login_failures,
    revoke_sessions,
    session_payload,
    set_session_cookie,
    verify_admin_password,
)
from ..db import get_db, log_event
from ..pages import page

router = APIRouter()


@router.get("/admin", response_class=HTMLResponse)
async def admin_panel(request: Request) -> Response:
    """The panel. The session is checked by its API calls: a 401 from /admin/api/status sends the page to
    /admin/login, so /admin itself does not check the cookie.
    """
    return HTMLResponse(page("admin.html"))


@router.get("/admin/login", response_class=HTMLResponse)
async def admin_login_form(request: Request) -> Response:
    return HTMLResponse(page("login.html"))


@router.post("/admin/login")
async def admin_login_submit(
    request: Request,
    password: str = Form(...),
    db: sqlite3.Connection = Depends(get_db),
) -> Response:
    """Password check: session cookie and /admin, or back to the form with an error."""
    ip = request.client.host if request.client else "unknown"
    wait = login_lockout_remaining(ip)
    if wait:
        # Not even verified: no information leak and no argon2 CPU spent while locked.
        return RedirectResponse(url=f"/admin/login?error=locked&wait={wait}", status_code=303)

    if not verify_admin_password(db, password):
        lockout = record_login_failure(ip)
        log_event(db, "admin_login_failed", {"ip": ip})
        if lockout:
            log_event(db, "admin_login_locked", {"ip": ip, "seconds": lockout})
            return RedirectResponse(url=f"/admin/login?error=locked&wait={lockout}", status_code=303)
        return RedirectResponse(url="/admin/login?error=1", status_code=303)

    reset_login_failures(ip)
    log_event(db, "admin_login_success")
    cookie_value = issue_session_cookie(db)
    response = RedirectResponse(url="/admin", status_code=303)
    set_session_cookie(response, cookie_value)
    return response


@router.post("/admin/logout")
async def admin_logout(request: Request, db: sqlite3.Connection = Depends(get_db)) -> Response:
    """Ends every session (a copy of this cookie too) and goes to the login form.

    Only a valid session revokes: an anonymous request (no cookie, an expired one) just clears the cookie,
    so nobody can log the parent out from outside. Logging out on one device logs out the others too.
    """
    try:
        session_payload(db, request.cookies.get(SESSION_COOKIE))
        revoke_sessions(db)
    except HTTPException:
        pass
    response = RedirectResponse(url="/admin/login", status_code=303)
    clear_session_cookie(response)
    return response
