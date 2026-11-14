from datetime import UTC, date, datetime, time, timedelta
from zoneinfo import ZoneInfo

from app.config import week_schedule
from app.endpoints.totp import _compute_night_extension_until
from app.lock_logic import (
    compute_lock_reason,
    current_or_next_night_start,
    daily_limit,
    local_to_utc,
    night_window_at,
)

TZ = ZoneInfo("Europe/Warsaw")
CFG = {"days": week_schedule(120, 240, "21:30", "06:30")}

# 2026-11-02 is a Monday.
MON, TUE, WED, THU, FRI, SAT, SUN = (date(2026, 11, 2) + timedelta(days=i) for i in range(7))


def state(**kw) -> dict:
    base = {
        "manual_lock": 0,
        "night_extension_until_utc": None,
        "seconds_used": 0,
        "limit_override_minutes": 0,
        "pause_active": 0,
    }
    return base | kw


def at(hh: int, mm: int, d: date = WED) -> datetime:
    return datetime.combine(d, time(hh, mm), tzinfo=TZ).astimezone(UTC)


def reason(now: datetime, cfg: dict = CFG, **kw):
    return compute_lock_reason(now, TZ, now.astimezone(TZ).date(), state(**kw), cfg)


def week(**changes) -> dict:
    """CFG with some days changed: week(fri={"night_start": "23:00"})."""
    days = [dict(d) for d in CFG["days"]]
    for name, fields in changes.items():
        days[("mon", "tue", "wed", "thu", "fri", "sat", "sun").index(name)].update(fields)
    return {"days": days}


def locked_at(cfg: dict, d: date, hh: int, mm: int) -> bool:
    return night_window_at(cfg, datetime.combine(d, time(hh, mm))) is not None


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
    assert locked_at(CFG, WED, 23, 0)
    assert locked_at(CFG, THU, 3, 0)
    assert not locked_at(CFG, THU, 6, 30)
    assert not locked_at(CFG, WED, 12, 0)


def test_wizard_week_counts_friday_as_weekend():
    """week_schedule (the default week's shape): Mon–Thu one limit, Fri–Sun the other."""
    assert [daily_limit(CFG, d) for d in (MON, TUE, WED, THU, FRI, SAT, SUN)] == [120] * 4 + [240] * 3


def test_each_day_has_its_own_limit():
    cfg = week(tue={"daily_limit": 30}, sat={"daily_limit": 300})
    assert [daily_limit(cfg, d) for d in (MON, TUE, SAT, SUN)] == [120, 30, 300, 240]
    assert reason(at(12, 0, TUE), cfg, seconds_used=30 * 60) == "limit"
    assert reason(at(12, 0, MON), cfg, seconds_used=30 * 60) is None


def test_night_belongs_to_the_evening_it_starts():
    """Decision #48: "Friday 23:00–09:00" is the night from Friday to Saturday."""
    cfg = week(fri={"night_start": "23:00", "night_end": "09:00"})
    assert locked_at(cfg, THU, 22, 0)        # Thursday's night: 21:30
    assert locked_at(cfg, FRI, 6, 0)         # ... until Friday 06:30
    assert not locked_at(cfg, FRI, 22, 0)    # Friday's night starts at 23:00
    assert locked_at(cfg, FRI, 23, 30)
    assert locked_at(cfg, SAT, 8, 0)         # Friday's night ends Saturday 09:00
    assert not locked_at(cfg, SAT, 9, 0)
    assert locked_at(cfg, SAT, 22, 0)        # Saturday's own night: 21:30
    assert not locked_at(cfg, SUN, 8, 0)     # ... until Sunday 06:30


def test_night_start_before_noon_is_after_midnight():
    cfg = week(fri={"night_start": "00:30", "night_end": "09:00"})
    assert not locked_at(cfg, FRI, 23, 30)   # Friday evening is free
    assert not locked_at(cfg, SAT, 0, 15)
    assert locked_at(cfg, SAT, 0, 45)
    assert locked_at(cfg, SAT, 8, 59)
    assert locked_at(cfg, FRI, 1, 0)         # Friday morning still has Thursday's night


def test_night_within_one_evening():
    cfg = week(wed={"night_start": "20:00", "night_end": "23:00"})
    assert locked_at(cfg, WED, 22, 0)
    assert not locked_at(cfg, WED, 23, 30)


def test_night_start_for_grants():
    assert current_or_next_night_start(CFG, datetime(2026, 11, 4, 12, 0)) == datetime(2026, 11, 4, 21, 30)
    # After midnight the night in progress started the evening before.
    assert current_or_next_night_start(CFG, datetime(2026, 11, 5, 2, 0)) == datetime(2026, 11, 4, 21, 30)


def test_night_grant_counts_from_that_evenings_start():
    cfg = week(fri={"night_start": "23:00", "night_end": "09:00"})
    # Granted on Friday afternoon: Friday's night starts at 23:00, +60 min = until 00:00.
    _, until = _compute_night_extension_until(at(15, 0, FRI), TZ, 60, cfg)
    assert until.replace(tzinfo=None) == datetime(2026, 11, 7, 0, 0)
    # Granted on Saturday at 02:00, inside Friday's night: from now.
    _, until = _compute_night_extension_until(at(2, 0, SAT), TZ, 60, cfg)
    assert until.replace(tzinfo=None) == datetime(2026, 11, 7, 3, 0)



# === Change of time (#163): Europe/Warsaw, 2026-10-25 03:00 CEST → 02:00 CET, 2027-03-28 02:00 CET → 03:00 CEST ===


def test_local_to_utc_across_changes_of_time():
    assert local_to_utc(datetime(2026, 11, 4, 21, 30), TZ) == datetime(2026, 11, 4, 20, 30, tzinfo=UTC)
    # 02:30 happens twice in October: the first pass, as the night window's naive comparison.
    assert local_to_utc(datetime(2026, 10, 25, 2, 30), TZ) == datetime(2026, 10, 25, 0, 30, tzinfo=UTC)
    # 02:30 does not happen in March: the clock passes it at the jump, 02:00 CET = 01:00 UTC.
    assert local_to_utc(datetime(2027, 3, 28, 2, 30), TZ) == datetime(2027, 3, 28, 1, 0, tzinfo=UTC)


def test_night_shift_across_autumn_change_lasts_real_minutes():
    now = datetime(2026, 10, 25, 1, 30, tzinfo=TZ).astimezone(UTC)  # CEST, inside Saturday's night
    until_utc, until_local = _compute_night_extension_until(now, TZ, 120, CFG)
    assert until_utc - now == timedelta(minutes=120)  # was 180: 01:30 + 2 h on the wall clock
    assert until_local.strftime("%H:%M %Z") == "02:30 CET"


def test_night_shift_across_spring_change_lasts_real_minutes():
    now = datetime(2027, 3, 28, 1, 30, tzinfo=TZ).astimezone(UTC)  # CET, inside Saturday's night
    until_utc, until_local = _compute_night_extension_until(now, TZ, 120, CFG)
    assert until_utc - now == timedelta(minutes=120)  # was 60
    assert until_local.strftime("%H:%M %Z") == "04:30 CEST"


def test_night_starting_in_the_skipped_hour():
    """A night from 02:30 on the night the clock jumps from 02:00 to 03:00: it starts at the jump."""
    cfg = week(sat={"night_start": "02:30", "night_end": "09:00"})
    sat = date(2027, 3, 27)
    assert reason(datetime(2027, 3, 28, 1, 59, tzinfo=TZ).astimezone(UTC), cfg) is None
    assert reason(datetime(2027, 3, 28, 3, 0, tzinfo=TZ).astimezone(UTC), cfg) == "night"
    # Granted in the evening: an hour from the start of the night, not from a 03:30 that 02:30+01:00 would give.
    until_utc, until_local = _compute_night_extension_until(at(20, 0, sat), TZ, 60, cfg)
    assert until_utc == datetime(2027, 3, 28, 2, 0, tzinfo=UTC)
    assert until_local.strftime("%H:%M %Z") == "04:00 CEST"


def test_night_spanning_autumn_change_ends_by_the_wall_clock():
    second_0230 = datetime(2026, 10, 25, 2, 30, fold=1, tzinfo=TZ).astimezone(UTC)  # CET
    assert reason(second_0230) == "night"
    assert reason(datetime(2026, 10, 25, 6, 29, tzinfo=TZ).astimezone(UTC)) == "night"
    assert reason(datetime(2026, 10, 25, 6, 30, tzinfo=TZ).astimezone(UTC)) is None

def test_limit_zero_means_no_limit():
    """#106: a day with limit 0 has no limit lock, whatever the time used; the night and the manual lock stay."""
    cfg = week(wed={"daily_limit": 0})
    assert reason(at(12, 0), cfg, seconds_used=10**6) is None
    assert reason(at(12, 0), cfg, seconds_used=10**6, pause_active=1) == "pause"
    assert reason(at(23, 0), cfg) == "night"
    assert reason(at(12, 0), cfg, manual_lock=1) == "manual"
    assert reason(at(12, 0, THU), cfg, seconds_used=120 * 60) == "limit"  # the other days keep theirs
