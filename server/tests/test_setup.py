"""Setup wizard (/setup/complete): the first child and its week."""

import pytest
from fastapi.testclient import TestClient

import app.admin_auth
import app.db

WIZARD = {"child_name": "Ola", "password": "haslo-rodzica", "watchdog_server_url": "http://10.0.0.2:8088",
          "daily_limit_weekday": 90, "daily_limit_weekend": 200, "night_lock_start": "21:00",
          "night_lock_end": "07:00"}


@pytest.fixture
def fresh(tmp_path, monkeypatch):
    monkeypatch.setattr(app.db, "DATABASE_PATH", tmp_path / "vigil.db")
    app.admin_auth._login_failures.clear()
    from app.main import app as fastapi_app

    with TestClient(fastapi_app) as c:
        yield c


def test_wizard_week_has_the_old_shape(fresh):
    """The wizard still asks Mon–Thu / Fri–Sun and one night (#48); the panel edits each day."""
    assert fresh.post("/setup/complete", json=WIZARD).json() == {"ok": True}
    week = fresh.get("/admin/api/status").json()["child"]["schedule"]
    assert week == ([{"daily_limit": 90, "night_start": "21:00", "night_end": "07:00"}] * 4
                    + [{"daily_limit": 200, "night_start": "21:00", "night_end": "07:00"}] * 3)


def test_wizard_rejects_an_empty_night(fresh):
    r = fresh.post("/setup/complete", json={**WIZARD, "night_lock_end": "21:00"})
    assert r.status_code == 422
    assert r.json()["detail"] == {"night_lock_end": "equals_start"}
