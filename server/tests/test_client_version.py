"""The client version in Settings (#159): reported in the heartbeat, compared with the newest client release."""

import importlib.util
from pathlib import Path

import pytest

import app.versions as versions
from app.crypto import hash_password
from app.db import get_connection, set_config_value
from app.versions import update_needed

_spec = importlib.util.spec_from_file_location(
    "release_notes", Path(__file__).resolve().parents[2] / ".github" / "scripts" / "release_notes.py")
release_notes = importlib.util.module_from_spec(_spec)
_spec.loader.exec_module(release_notes)


def device(client) -> dict:
    return client.get("/admin/api/status").json()["child"]["devices"][0]


def login(client) -> None:
    with get_connection() as conn:
        set_config_value(conn, "parent_password_hash", hash_password("haslo-rodzica"))
    assert client.post("/admin/login", data={"password": "haslo-rodzica"}).status_code == 200


@pytest.mark.parametrize(("pc", "newest", "needed"), [
    ("0.9.0", "1.0.0", True),
    ("1.0.0", "1.0.0", False),
    ("1.0.1", "1.0.0", False),        # a client newer than the server knows
    ("1.0.0", "1.0.10", True),        # numbers, not text
    (None, "1.0.0", True),            # clients up to 0.9 send no version
    ("0.0.0-ci.89", "1.0.0", False),  # a CI or local build is never flagged
    ("0.0.0-dev", "1.0.0", False),
    ("0.9.0", "", False),             # a server built outside a release: nothing to compare with
    (None, "", False),
])
def test_update_needed(monkeypatch, pc, newest, needed):
    monkeypatch.setattr(versions, "CLIENT_VERSION", newest)
    assert update_needed(pc) is needed


def test_heartbeat_reports_the_version(watchdog, client, monkeypatch):
    monkeypatch.setattr(versions, "CLIENT_VERSION", "1.0.0")
    login(client)
    watchdog.heartbeat()
    assert (device(client)["client_version"], device(client)["update_needed"]) == (None, True)
    watchdog.heartbeat(client_version="0.9.0")
    assert (device(client)["client_version"], device(client)["update_needed"]) == ("0.9.0", True)
    watchdog.heartbeat(client_version="1.0.0")
    assert (device(client)["client_version"], device(client)["update_needed"]) == ("1.0.0", False)
    # The last heartbeat counts: an older client put back on the PC sends none.
    watchdog.heartbeat()
    assert device(client)["client_version"] is None


def test_a_long_version_is_refused(watchdog):
    r = watchdog.post("/v1/heartbeat", {"session_active": True, "user_active": True,
                                         "current_time_utc": "2026-11-15T08:00:00+00:00", "client_version": "1" * 65})
    assert r.status_code == 422


CHANGELOG = """# Changelog

## [Unreleased]

## [1.0.2] - 2026-12-10

Server fix.

| | Update |
|---|---|
| Server | **Yes.** New image. |
| Children's PCs | **Not needed.** The client did not change. |

## [1.0.1] - 2026-12-05

Lock screen fix.

| | Update |
|---|---|
| Server | **Not needed.** |
| Children's PCs | **Yes.** Run the new `vigil_setup.exe` → **Update**. |

## [1.0.0] - 2026-12-01

First.

| | Update |
|---|---|
| Server | **Yes.** |
| Children's PCs | **Yes.** |

[1.0.0]: https://example.invalid
"""


@pytest.mark.parametrize(("tag", "client"), [("1.0.2", "1.0.1"), ("1.0.1", "1.0.1"), ("1.0.0", "1.0.0")])
def test_client_version_of_a_release(tag, client):
    """The newest section up to the tag whose Children's PCs row says Yes (Optional or Not needed: the PCs may
    stay)."""
    assert release_notes.client_version(CHANGELOG, tag) == client


def test_client_version_needs_the_tags_section():
    with pytest.raises(SystemExit):
        release_notes.client_version(CHANGELOG, "1.0.3")
