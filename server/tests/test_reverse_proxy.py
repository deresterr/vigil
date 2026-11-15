"""Behind a reverse proxy (#162): uvicorn's ProxyHeadersMiddleware, as `uvicorn` builds it from FORWARDED_ALLOW_IPS
(compose.yaml), in front of the app. The TestClient connects as "testclient", so that literal plays the proxy."""

import pytest
from fastapi.testclient import TestClient
from uvicorn.middleware.proxy_headers import ProxyHeadersMiddleware

from app.crypto import hash_password
from app.db import get_connection, set_config_value
from app.main import app as fastapi_app

PASSWORD = "haslo-rodzica"


def behind_proxy(trusted: str) -> TestClient:
    return TestClient(ProxyHeadersMiddleware(fastapi_app, trusted_hosts=trusted))


@pytest.fixture
def password(client):
    with get_connection() as conn:
        set_config_value(conn, "parent_password_hash", hash_password(PASSWORD))


def login(c: TestClient, pw: str, ip: str, proto: str = "http"):
    return c.post("/admin/login", data={"password": pw}, follow_redirects=False,
                  headers={"X-Forwarded-For": ip, "X-Forwarded-Proto": proto})


def lock_out(c: TestClient, ip: str) -> None:
    for _ in range(5):
        r = login(c, "wrong", ip)
    assert "error=locked" in r.headers["location"]


def test_trusted_proxy_keeps_login_limits_per_client(password):
    """The child locking itself out on its PC does not lock the parent's phone out."""
    c = behind_proxy("testclient")
    lock_out(c, "192.0.2.10")
    assert login(c, PASSWORD, "192.0.2.10").headers["location"].startswith("/admin/login?error=locked")
    assert login(c, PASSWORD, "192.0.2.20").headers["location"] == "/admin"


def test_untrusted_proxy_headers_are_ignored(password):
    """FORWARDED_ALLOW_IPS empty (compose's default): the headers change nothing, so nobody gets round the limit
    with a made-up X-Forwarded-For — and behind a proxy everyone shares its address."""
    c = behind_proxy("")
    lock_out(c, "192.0.2.10")
    assert "error=locked" in login(c, PASSWORD, "192.0.2.20").headers["location"]


@pytest.mark.parametrize(("trusted", "proto", "secure"), [
    ("testclient", "https", True),    # TLS at the proxy
    ("testclient", "http", False),
    ("", "https", False),             # an X-Forwarded-Proto nobody vouches for
])
def test_session_cookie_is_secure_over_https(password, trusted, proto, secure):
    r = login(behind_proxy(trusted), PASSWORD, "192.0.2.20", proto)
    assert r.headers["location"] == "/admin"
    assert ("secure" in r.headers["set-cookie"].lower()) is secure
