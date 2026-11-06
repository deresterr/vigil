from datetime import UTC, date, datetime, time, timedelta
from zoneinfo import ZoneInfo

from app.lock_logic import compute_lock_reason, is_in_night_window, is_weekend

TZ = ZoneInfo("Europe/Warsaw")
CFG = {
    "night_lock_start": "21:30",
    "night_lock_end": "06:30",
    "daily_limit_weekday": 120,
    "daily_limit_weekend": 240,
}


def state(**kw) -> dict:
    base = {
        "manual_lock": 0,
        "night_extension_until_utc": None,
        "seconds_used": 0,
        "limit_override_minutes": 0,
        "pause_active": 0,
    }
    return base | kw


def at(hh: int, mm: int, d: date = date(2026, 11, 4)) -> datetime:  # Wednesday
    return datetime.combine(d, time(hh, mm), tzinfo=TZ).astimezone(UTC)


def reason(now: datetime, **kw):
    return compute_lock_reason(now, TZ, now.astimezone(TZ).date(), state(**kw), CFG)


def test_priority_manual_over_everything():
    assert reason(at(23, 0), manual_lock=1, seconds_used=10**6, pause_active=1) == "manual"


def test_priority_night_over_limit_and_pause():
    assert reason(at(23, 0), seconds_used=10**6, pause_active=1) == "night"


def test_priority_limit_over_pause():
    assert reason(at(12, 0), seconds_used=120 * 60, pause_active=1) == "limit"


def test_pause_only_when_nothing_else():
    assert reason(at(12, 0), pause_active=1) == "pause"
    assert reason(at(12, 0)) is None


def test_limit_override_adds_minutes():
    assert reason(at(12, 0), seconds_used=150 * 60, limit_override_minutes=60) is None


def test_night_extension_suppresses_night():
    now = at(22, 0)
    assert reason(now, night_extension_until_utc=(now + timedelta(minutes=30)).isoformat()) is None


def test_night_window_crosses_midnight():
    s, e = time(21, 30), time(6, 30)
    assert is_in_night_window(time(23, 0), s, e)
    assert is_in_night_window(time(3, 0), s, e)
    assert not is_in_night_window(time(6, 30), s, e)
    assert not is_in_night_window(time(12, 0), s, e)


def test_friday_counts_as_weekend():
    # Current behaviour (hardcoded). See docs/review-2026-11.md: per-weekday limits.
    assert [is_weekend(date(2026, 11, d)) for d in range(2, 9)] == [False] * 4 + [True] * 3
