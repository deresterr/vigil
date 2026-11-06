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
