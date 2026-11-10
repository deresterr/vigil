from app.admin_auth import login_lockout_remaining, record_login_failure, reset_login_failures
from app.crypto import hash_password
from app.db import get_connection, set_config_value

PASSWORD = "haslo-rodzica"


def _set_password():
    with get_connection() as conn:
        set_config_value(conn, "parent_password_hash", hash_password(PASSWORD))


def _login(client, password):
    return client.post("/admin/login", data={"password": password}, follow_redirects=False)


def test_lockout_schedule_is_capped():
    ip, now, seen = "10.0.0.5", 1000.0, []
    for _ in range(20):
        lock = record_login_failure(ip, now)
        if lock:
            seen.append(lock)
            assert login_lockout_remaining(ip, now) == lock
            now += lock
            assert login_lockout_remaining(ip, now) == 0
    assert seen == [60, 300, 900, 900]


def test_limits_are_per_ip_and_reset_on_success():
    for _ in range(5):
        record_login_failure("10.0.0.5", 0.0)
    assert login_lockout_remaining("10.0.0.5", 1.0) > 0
    assert login_lockout_remaining("10.0.0.9", 1.0) == 0
    reset_login_failures("10.0.0.5")
    assert login_lockout_remaining("10.0.0.5", 1.0) == 0


def test_login_endpoint_locks_after_five_failures(client):
    _set_password()
    for _ in range(4):
        assert _login(client, "zle").headers["location"] == "/admin/login?error=1"
    assert _login(client, "zle").headers["location"] == "/admin/login?error=locked&wait=60"
    # even the right password is not evaluated while locked
    r = _login(client, PASSWORD)
    assert r.headers["location"].startswith("/admin/login?error=locked")
    assert "set-cookie" not in r.headers


def test_successful_login_clears_failures(client):
    _set_password()
    for _ in range(4):
        _login(client, "zle")
    assert _login(client, PASSWORD).headers["location"] == "/admin"
    for _ in range(4):
        assert _login(client, "zle").headers["location"] == "/admin/login?error=1"


def test_wrong_current_password_keeps_the_session(client):
    """A typo in the current password is a 403: a 401 sent the panel to the login page (logged out)."""
    _set_password()
    assert _login(client, PASSWORD).status_code == 303
    r = client.post("/admin/api/change_password", json={"current": "zle-haslo", "new": "nowe-haslo-1"})
    assert (r.status_code, r.json()["detail"]) == (403, "wrong_current_password")
    assert client.get("/admin/api/status").status_code == 200
    r = client.post("/admin/api/change_password", json={"current": PASSWORD, "new": "nowe-haslo-1"})
    assert r.json() == {"ok": True}
    client.post("/admin/logout")
    assert _login(client, "nowe-haslo-1").status_code == 303


def _status_with(client, cookie):
    client.cookies.clear()
    client.cookies.set("vigil_session", cookie)
    return client.get("/admin/api/status").status_code


def test_logout_revokes_a_copied_cookie(client):
    """The parent logs in on the child's PC (one Windows account): a copy of the cookie dies at logout (#86)."""
    _set_password()
    _login(client, PASSWORD)
    copied = client.cookies.get("vigil_session")
    client.post("/admin/logout")
    client.cookies.clear()
    assert _status_with(client, copied) == 401


def test_anonymous_logout_revokes_nothing(client):
    _set_password()
    _login(client, PASSWORD)
    session = client.cookies.get("vigil_session")
    client.cookies.clear()
    client.post("/admin/logout")
    assert _status_with(client, session) == 200


def test_password_change_ends_other_sessions_and_keeps_this_one(client):
    _set_password()
    _login(client, PASSWORD)
    other = client.cookies.get("vigil_session")  # e.g. the same login on another device
    r = client.post("/admin/api/change_password", json={"current": PASSWORD, "new": "nowe-haslo-1"})
    assert r.json() == {"ok": True}
    assert client.get("/admin/api/status").status_code == 200  # the fresh cookie from the response
    client.cookies.clear()
    assert _status_with(client, other) == 401
