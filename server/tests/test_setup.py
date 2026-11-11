"""Setup wizard (/setup/complete): the first child and its week."""

import json

import pytest
from fastapi.testclient import TestClient

import app.admin_auth
import app.db
from app.config import default_schedule
from app.pages import page

# Each day different, as the wizard's grid can send it (#102)
WEEK = [{"daily_limit": 60 + 10 * i, "night_start": f"2{i % 4}:00", "night_end": f"0{6 + i % 3}:30"}
        for i in range(7)]
WIZARD = {"child_name": "Ola", "password": "haslo-rodzica", "timezone": "Europe/Warsaw", "schedule": WEEK}


def _week(**day3):
    return [*WEEK[:3], {**WEEK[3], **day3}, *WEEK[4:]]


@pytest.fixture
def fresh(tmp_path, monkeypatch):
    monkeypatch.setattr(app.db, "DATABASE_PATH", tmp_path / "vigil.db")
    app.admin_auth._login_failures.clear()
    from app.main import app as fastapi_app

    with TestClient(fastapi_app) as c:
        yield c


def test_wizard_saves_the_week_day_by_day(fresh):
    """The wizard's grid is the panel's: the first child gets exactly the seven days sent (#102)."""
    assert fresh.post("/setup/complete", json=WIZARD).json() == {"ok": True}
    assert fresh.get("/admin/api/status").json()["child"]["schedule"] == WEEK


def test_wizard_refuses_the_old_two_limit_fields(fresh):
    """A page cached from before #102 sends the old fields without `schedule`: refused, nothing half-saved."""
    old = {"child_name": "Ola", "password": "haslo-rodzica", "timezone": "UTC", "daily_limit_weekday": 90,
           "daily_limit_weekend": 200, "night_lock_start": "21:00", "night_lock_end": "07:00"}
    r = fresh.post("/setup/complete", json=old)
    assert (r.status_code, r.json()["detail"]) == (422, {"schedule": "missing"})


def test_wizard_grid_starts_from_the_default_week(fresh):
    """The grid and the "Default week" template come from config.default_schedule, as a panel-added child's."""
    html = page("setup.html")
    assert "const DEFAULT_WEEK = [" in html
    sent = html.split("const DEFAULT_WEEK = ", 1)[1].split(";\n", 1)[0]
    assert json.loads(sent) == default_schedule()


@pytest.mark.parametrize(("body", "detail"), [
    ({**WIZARD, "timezone": ""}, {"timezone": "invalid"}),
    ({**WIZARD, "timezone": "Mars/Base"}, {"timezone": "invalid"}),
    ({**WIZARD, "schedule": WEEK[:6]}, {"schedule": "too_short"}),
    ({**WIZARD, "schedule": _week(daily_limit=1441)}, {"schedule.3.daily_limit": "less_than_equal"}),
    ({**WIZARD, "schedule": _week(daily_limit="90")}, {"schedule.3.daily_limit": "int_type"}),
    ({**WIZARD, "schedule": _week(night_start="9:30")}, {"schedule.3.night_start": "string_pattern_mismatch"}),
    ({**WIZARD, "schedule": _week(night_end=WEEK[3]["night_start"])}, {"schedule.3": "night_start_equals_end"}),
    ({**WIZARD, "password": "short"}, {"password": "string_too_short"}),
])
def test_wizard_reports_bad_fields(fresh, body, detail):
    """Every problem is a 422 with field → code (setup.html shows it), never a 500 (#86)."""
    r = fresh.post("/setup/complete", json=body)
    assert (r.status_code, r.json()["detail"]) == (422, detail)


def test_wizard_rejects_a_body_that_is_not_an_object(fresh):
    assert fresh.post("/setup/complete", json=[1, 2]).status_code == 422


def test_wizard_keeps_the_password_as_typed(fresh):
    assert fresh.post("/setup/complete", json={**WIZARD, "password": " spaces at both ends "}).status_code == 200
    fresh.cookies.clear()
    r = fresh.post("/admin/login", data={"password": " spaces at both ends "}, follow_redirects=False)
    assert r.headers["location"] == "/admin"
