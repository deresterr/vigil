"""JSON API of the parent panel (/admin/api/*). Every endpoint needs the session cookie.

Children and devices (D7):
- GET    /admin/api/status?child_id=        — overview of all children + details of one
- POST   /admin/api/children                — add a child
- PATCH  /admin/api/children/{id}           — name, weekly schedule (limit + night window per day, #48)
- DELETE /admin/api/children/{id}           — only without devices
- POST   /admin/api/children/{id}/grant     — extra time (limit or night)
- POST   /admin/api/children/{id}/manual_lock
- POST   /admin/api/children/{id}/clear_extension
- PATCH  /admin/api/devices/{id}            — rename
- DELETE /admin/api/devices/{id}            — unpair (its key stops working: the PC fails closed)
- POST   /admin/api/pairing_code            — one-time code: new device of a child, or re-pair a device
- DELETE /admin/api/pairing_code            — cancel it
Server-wide:
- GET/POST /admin/api/config                — timezone, heartbeat_grace_seconds
- GET /admin/api/totp, POST /admin/api/regen_totp — the one TOTP secret for all children
- POST /admin/api/change_password
"""

from __future__ import annotations

import io
import sqlite3
from datetime import datetime, timezone
from typing import Annotated
from zoneinfo import ZoneInfo, ZoneInfoNotFoundError

import pyotp
import qrcode
from fastapi import APIRouter, Depends, HTTPException
from pydantic import BaseModel, ConfigDict, Field, field_validator, model_validator

from ..admin_auth import require_admin_session, verify_admin_password
from ..config import TOTP_ISSUER, default_schedule
from ..crypto import hash_password
from ..db import (
    ensure_child_day,
    get_child,
    get_config_int,
    get_config_value,
    get_db,
    log_event,
    set_config_value,
    update_child_day,
)
from ..lock_logic import child_today, night_window, night_window_at, parse_iso_utc
from .journal import ALERT_WHERE
from .pairing import active_pairing_expiry, clear_pairing_code, create_pairing_code
from .totp import _compute_night_extension_until

router = APIRouter(prefix="/admin/api", dependencies=[Depends(require_admin_session)])

HHMM = Annotated[str, Field(pattern=r"^([01]\d|2[0-3]):[0-5]\d$")]
Minutes = Annotated[int, Field(ge=0, le=1440)]
Name = Annotated[str, Field(min_length=1, max_length=40)]


class _Body(BaseModel):
    model_config = ConfigDict(strict=True, extra="forbid", str_strip_whitespace=True)


def _now_tz(db: sqlite3.Connection) -> tuple[datetime, ZoneInfo]:
    return datetime.now(timezone.utc), ZoneInfo(get_config_value(db, "timezone"))


def _child_or_404(db: sqlite3.Connection, child_id: int) -> sqlite3.Row:
    child = get_child(db, child_id)
    if child is None:
        raise HTTPException(status_code=404, detail="child_not_found")
    return child


# === Status ===


def _device_view(row: sqlite3.Row, now_utc: datetime, tz: ZoneInfo, grace: int, today: str) -> dict:
    ago = None
    online = False
    last_local = None
    if row["last_heartbeat_utc"]:
        last = parse_iso_utc(row["last_heartbeat_utc"])
        ago = int((now_utc - last).total_seconds())
        online = ago <= grace * 2  # 2x grace: a bit tolerant for the panel
        last_local = last.astimezone(tz).strftime("%Y-%m-%d %H:%M")
    active_now = online and bool(row["last_active_utc"]) and \
        (now_utc - parse_iso_utc(row["last_active_utc"])).total_seconds() <= grace * 2
    return {
        "id": row["id"],
        "name": row["name"],
        "online": online,
        "active_now": active_now,
        "last_heartbeat_ago_seconds": ago,
        "last_heartbeat_local": last_local,
        "paired_local": parse_iso_utc(row["paired_utc"]).astimezone(tz).strftime("%Y-%m-%d"),
        "minutes_today": (row["seconds_today"] // 60) if row["seconds_date"] == today else 0,
    }


def _child_detail(db: sqlite3.Connection, child: sqlite3.Row, now_utc: datetime, tz: ZoneInfo,
                  devices: list[dict]) -> dict:
    t = child_today(db, child, now_utc, tz)
    state, cfg = t["state"], t["cfg"]
    night_ext_local = None
    if state["night_extension_until_utc"]:
        ext = parse_iso_utc(state["night_extension_until_utc"])
        if now_utc < ext:
            night_ext_local = ext.astimezone(tz).strftime("%Y-%m-%d %H:%M")
    now_local = now_utc.astimezone(tz)
    # Tonight's window, or the one in progress (after midnight it belongs to yesterday's evening).
    night = night_window_at(cfg, now_local) or night_window(cfg, t["today"])
    return {
        "id": child["id"],
        "name": child["name"],
        "today_local": t["today"].isoformat(),
        "today_weekday": t["today"].weekday(),
        "current_time_local": now_local.strftime("%Y-%m-%d %H:%M:%S"),
        "lock_required": t["lock_reason"] is not None,
        "lock_reason": t["lock_reason"],
        "minutes_used": t["minutes_used"],
        "minutes_remaining": t["minutes_remaining"],
        "daily_limit": t["daily_limit"],
        "limit_override_minutes": state["limit_override_minutes"],
        "effective_limit": t["effective_limit"],
        "manual_lock": bool(state["manual_lock"]),
        "pause_active": bool(state["pause_active"]),
        "night_extension_until_local": night_ext_local,
        "in_night_window": night_window_at(cfg, now_local) is not None,
        "night_local": [dt.strftime("%Y-%m-%d %H:%M") for dt in night],
        "schedule": cfg["days"],
        "devices": devices,
    }


@router.get("/status")
async def status(db: sqlite3.Connection = Depends(get_db), child_id: int | None = None) -> dict:
    """Everything the panel polls every 5 s: all children in brief, one child (child_id, default the
    first) in full. `child` is null while there is no child yet."""
    now_utc, tz = _now_tz(db)
    grace = get_config_int(db, "heartbeat_grace_seconds")
    today = now_utc.astimezone(tz).date().isoformat()
    children = db.execute("SELECT * FROM children ORDER BY id").fetchall()
    unacked = dict(db.execute(
        f"SELECT child_id, COUNT(*) FROM events WHERE {ALERT_WHERE} AND acked_utc IS NULL GROUP BY child_id"
    ).fetchall())

    summaries, detail = [], None
    devices_online = devices_total = 0
    for child in children:
        devices = [_device_view(d, now_utc, tz, grace, today) for d in
                   db.execute("SELECT * FROM devices WHERE child_id = ? ORDER BY id", (child["id"],))]
        online = sum(d["online"] for d in devices)
        devices_online += online
        devices_total += len(devices)
        summaries.append({
            "id": child["id"],
            "name": child["name"],
            "lock_reason": child_today(db, child, now_utc, tz)["lock_reason"],
            "alerts_unacked": unacked.get(child["id"], 0),
            "devices_online": online,
            "devices_total": len(devices),
        })
        if detail is None and child_id in (None, child["id"]):
            detail = _child_detail(db, child, now_utc, tz, devices)
    if child_id is not None and detail is None:
        raise HTTPException(status_code=404, detail="child_not_found")

    return {
        "children": summaries,
        "child": detail,
        "devices_online": devices_online,
        "devices_total": devices_total,
        "alerts_unacked": sum(unacked.values()),
        "pairing_active": active_pairing_expiry(db, now_utc) is not None,
    }


# === Children ===


class DayPlan(_Body):
    """One day of the week. The night belongs to this day's evening; a start before 12:00 is after
    midnight (lock_logic.night_window)."""
    daily_limit: Minutes
    night_start: HHMM
    night_end: HHMM

    @model_validator(mode="after")
    def _night_not_empty(self) -> DayPlan:
        if self.night_start == self.night_end:
            raise ValueError("night_start_equals_end")
        return self


# Monday first (date.weekday()).
Week = Annotated[list[DayPlan], Field(min_length=7, max_length=7)]


class ChildCreate(_Body):
    name: Name
    schedule: Week = Field(default_factory=lambda: [DayPlan(**d) for d in default_schedule()])


class ChildUpdate(_Body):
    name: Name | None = None
    schedule: Week | None = None


def _write_schedule(db: sqlite3.Connection, child_id: int, week: list[DayPlan]) -> None:
    db.executemany(
        "INSERT INTO child_schedule(child_id, weekday, daily_limit, night_start, night_end) VALUES (?, ?, ?, ?, ?) "
        "ON CONFLICT(child_id, weekday) DO UPDATE SET daily_limit = excluded.daily_limit, "
        "night_start = excluded.night_start, night_end = excluded.night_end",
        [(child_id, weekday, d.daily_limit, d.night_start, d.night_end) for weekday, d in enumerate(week)],
    )


def insert_child(db: sqlite3.Connection, body: ChildCreate) -> int:
    """Shared with the setup wizard."""
    child_id = db.execute("INSERT INTO children(name, created_utc) VALUES (?, ?)",
                          (body.name, datetime.now(timezone.utc).isoformat())).lastrowid
    _write_schedule(db, child_id, body.schedule)
    return child_id


@router.post("/children")
async def create_child(body: ChildCreate, db: sqlite3.Connection = Depends(get_db)) -> dict:
    child_id = insert_child(db, body)
    log_event(db, "child_added", {"name": body.name}, child_id=child_id)
    return {"ok": True, "id": child_id}


@router.patch("/children/{child_id}")
async def update_child(child_id: int, body: ChildUpdate, db: sqlite3.Connection = Depends(get_db)) -> dict:
    _child_or_404(db, child_id)
    updated = []
    if body.name is not None:
        db.execute("UPDATE children SET name = ? WHERE id = ?", (body.name, child_id))
        updated.append("name")
    if body.schedule is not None:
        _write_schedule(db, child_id, body.schedule)
        updated.append("schedule")
    if updated:
        log_event(db, "config_updated", {"keys": updated, "via": "admin_ui"}, child_id=child_id)
    return {"ok": True, "updated": updated}


@router.delete("/children/{child_id}")
async def delete_child(child_id: int, db: sqlite3.Connection = Depends(get_db)) -> dict:
    child = _child_or_404(db, child_id)
    if db.execute("SELECT 1 FROM devices WHERE child_id = ?", (child_id,)).fetchone():
        raise HTTPException(status_code=409, detail="child_has_devices")
    if get_config_value(db, "pairing_child_id") == str(child_id):
        clear_pairing_code(db)
    db.execute("DELETE FROM child_daily WHERE child_id = ?", (child_id,))
    db.execute("DELETE FROM child_schedule WHERE child_id = ?", (child_id,))
    db.execute("DELETE FROM children WHERE id = ?", (child_id,))
    # Journal entries stay (with their child_id); the journal shows them under the stored name.
    log_event(db, "child_removed", {"name": child["name"]}, child_id=child_id)
    return {"ok": True}


class GrantBody(_Body):
    minutes: Annotated[int, Field(gt=0, le=1440)]
    mode: Annotated[str, Field(pattern="^(limit|night)$")]


@router.post("/children/{child_id}/grant")
async def grant(child_id: int, body: GrantBody, db: sqlite3.Connection = Depends(get_db)) -> dict:
    """Extra time from the panel (the parent is logged in: no TOTP)."""
    child = _child_or_404(db, child_id)
    now_utc, tz = _now_tz(db)
    today = now_utc.astimezone(tz).date()
    state = ensure_child_day(db, child_id, today)

    if body.mode == "limit":
        # Atomic increment: several quick clicks must all count.
        db.execute("UPDATE child_daily SET limit_override_minutes = limit_override_minutes + ? "
                   "WHERE child_id = ? AND date_local = ?", (body.minutes, child_id, today.isoformat()))
        new_override = ensure_child_day(db, child_id, today)["limit_override_minutes"]
        log_event(db, "grant_applied", {"mode": "extend_limit", "minutes": body.minutes, "via": "admin_ui"},
                  child_id=child_id)
        return {"ok": True, "mode": "extend_limit", "minutes": body.minutes, "new_override": new_override}

    # Night: grants stack onto the end of a still-running extension.
    current_ext = None
    if state["night_extension_until_utc"]:
        parsed = parse_iso_utc(state["night_extension_until_utc"])
        if parsed > now_utc:
            current_ext = parsed
    ext_utc, ext_local = _compute_night_extension_until(
        now_utc, tz, body.minutes, child_today(db, child, now_utc, tz)["cfg"], current_extension_utc=current_ext)
    update_child_day(db, child_id, today, night_extension_until_utc=ext_utc.isoformat())
    until = ext_local.strftime("%Y-%m-%d %H:%M")
    log_event(db, "grant_applied", {"mode": "extend_night", "minutes": body.minutes, "via": "admin_ui",
                                    "until_local": until}, child_id=child_id)
    return {"ok": True, "mode": "extend_night", "minutes": body.minutes, "until_local": until}


class ManualLockBody(_Body):
    locked: bool


@router.post("/children/{child_id}/manual_lock")
async def set_manual_lock(child_id: int, body: ManualLockBody, db: sqlite3.Connection = Depends(get_db)) -> dict:
    _child_or_404(db, child_id)
    now_utc, tz = _now_tz(db)
    today = now_utc.astimezone(tz).date()
    ensure_child_day(db, child_id, today)
    update_child_day(db, child_id, today, manual_lock=int(body.locked))
    log_event(db, "manual_lock_set" if body.locked else "manual_lock_cleared", {"via": "admin_ui"},
              child_id=child_id)
    return {"ok": True, "manual_lock": body.locked}


@router.post("/children/{child_id}/clear_extension")
async def clear_extension(child_id: int, db: sqlite3.Connection = Depends(get_db)) -> dict:
    """Takes back today's extra time and night extension (the parent changed their mind)."""
    _child_or_404(db, child_id)
    now_utc, tz = _now_tz(db)
    today = now_utc.astimezone(tz).date()
    ensure_child_day(db, child_id, today)
    update_child_day(db, child_id, today, night_extension_until_utc=None, limit_override_minutes=0)
    log_event(db, "extensions_cleared", {"via": "admin_ui"}, child_id=child_id)
    return {"ok": True}


# === Devices and pairing ===


def _device_or_404(db: sqlite3.Connection, device_id: int) -> sqlite3.Row:
    row = db.execute("SELECT * FROM devices WHERE id = ?", (device_id,)).fetchone()
    if row is None:
        raise HTTPException(status_code=404, detail="device_not_found")
    return row


class DeviceUpdate(_Body):
    name: Name


@router.patch("/devices/{device_id}")
async def rename_device(device_id: int, body: DeviceUpdate, db: sqlite3.Connection = Depends(get_db)) -> dict:
    _device_or_404(db, device_id)
    db.execute("UPDATE devices SET name = ? WHERE id = ?", (body.name, device_id))
    return {"ok": True}


@router.delete("/devices/{device_id}")
async def unpair_device(device_id: int, db: sqlite3.Connection = Depends(get_db)) -> dict:
    """Its key is gone: the PC's requests fail authentication and it locks (fail-closed)."""
    row = _device_or_404(db, device_id)
    if get_config_value(db, "pairing_device_id") == str(device_id):
        clear_pairing_code(db)
    db.execute("DELETE FROM devices WHERE id = ?", (device_id,))
    log_event(db, "device_unpaired", {"name": row["name"]}, child_id=row["child_id"], device_id=device_id)
    return {"ok": True}


class PairingCodeBody(_Body):
    """Either a new device (child_id + device_name) or re-pairing an existing one (device_id)."""

    child_id: int | None = None
    device_name: Name | None = None
    device_id: int | None = None

    @model_validator(mode="after")
    def _one_target(self) -> PairingCodeBody:
        if (self.device_id is None) == (self.child_id is None):
            raise ValueError("give child_id or device_id")
        return self


@router.post("/pairing_code")
async def new_pairing_code(body: PairingCodeBody, db: sqlite3.Connection = Depends(get_db)) -> dict:
    """One-time code for Install.cmd. Generating a new one invalidates the previous one."""
    now = datetime.now(timezone.utc)
    if body.device_id is not None:
        device = _device_or_404(db, body.device_id)
        code, expires = create_pairing_code(db, now, device_id=device["id"])
        child_id = device["child_id"]
    else:
        child_id = _child_or_404(db, body.child_id)["id"]
        code, expires = create_pairing_code(db, now, child_id=child_id, device_name=body.device_name or "Komputer")
    log_event(db, "pairing_code_created", {"via": "admin_ui", "repair": body.device_id is not None},
              child_id=child_id, device_id=body.device_id)
    tz = ZoneInfo(get_config_value(db, "timezone"))
    return {"code": f"{code[:4]} {code[4:]}", "expires_local": expires.astimezone(tz).strftime("%H:%M"),
            "expires_in_seconds": int((expires - now).total_seconds())}


@router.delete("/pairing_code")
async def cancel_pairing_code(db: sqlite3.Connection = Depends(get_db)) -> dict:
    clear_pairing_code(db)
    return {"ok": True}


# === Server-wide config ===


class ConfigBody(_Body):
    timezone: str | None = None
    # Panel only ("online" = last heartbeat within 2x grace); the watchdog has its own grace.
    heartbeat_grace_seconds: Annotated[int, Field(ge=1, le=600)] | None = None

    @field_validator("timezone")
    @classmethod
    def _valid_tz(cls, v):
        if v is not None:
            try:
                ZoneInfo(v)
            except (ZoneInfoNotFoundError, ValueError) as e:
                raise ValueError("invalid_timezone") from e
        return v


@router.get("/config")
async def get_config(db: sqlite3.Connection = Depends(get_db)) -> dict:
    return {k: get_config_value(db, k) for k in ConfigBody.model_fields}


@router.post("/config")
async def update_config(body: ConfigBody, db: sqlite3.Connection = Depends(get_db)) -> dict:
    fields = body.model_dump(exclude_none=True)
    for key, value in fields.items():
        set_config_value(db, key, value)
    if fields:
        log_event(db, "config_updated", {"keys": sorted(fields), "via": "admin_ui"})
    return {"ok": True, "updated": sorted(fields)}


# === TOTP (one secret for all children) ===


def _png_qr_base64(data: str) -> str:
    """Render QR jako PNG, zwraca base64 string (do data: URL w <img src>).

    Wymuszamy PIL backend (Pillow). Domyślnie qrcode.make_image() detect-uje czy Pillow
    jest dostępny i fallbackuje do PyPNGImage (pure-Python) który ma inną sygnaturę save().
    Pillow jest hard requirement w requirements.txt — jeśli go brakuje, ImportError jest
    bardziej czytelny niż obecny TypeError z save(format=...).
    """
    import base64

    from qrcode.image.pil import PilImage
    qr = qrcode.QRCode(
        version=None,
        border=2,
        box_size=8,
        error_correction=qrcode.constants.ERROR_CORRECT_M,
    )
    qr.add_data(data)
    qr.make(fit=True)
    img = qr.make_image(image_factory=PilImage, fill_color="black", back_color="white")
    buf = io.BytesIO()
    img.save(buf, format="PNG")
    return base64.b64encode(buf.getvalue()).decode("ascii")


# === GET /admin/api/totp — zwraca obecny TOTP secret + QR ===


@router.get("/totp")
async def get_totp(db: sqlite3.Connection = Depends(get_db)) -> dict:
    """Zwraca AKTUALNY (nie regenerowany) TOTP secret + QR — do wyświetlenia w admin UI
    żeby rodzic mógł zeskanować ponownie (np. po wymianie telefonu)."""
    secret = get_config_value(db, "totp_secret")
    if not secret:
        raise HTTPException(status_code=404, detail="totp_not_configured")
    uri = pyotp.TOTP(secret).provisioning_uri(name="rodzic", issuer_name=TOTP_ISSUER)
    return {
        "secret": secret,
        "uri": uri,
        "qr_png_base64": _png_qr_base64(uri),
    }


@router.post("/regen_totp")
async def regen_totp(db: sqlite3.Connection = Depends(get_db)) -> dict:
    """Generuje nowy TOTP secret. Trzeba ponownie zeskanować QR aplikacją Aegis."""
    new_secret = pyotp.random_base32()
    set_config_value(db, "totp_secret", new_secret)
    set_config_value(db, "totp_last_step", 0)
    # Reset lockoutu — żeby admin nie musiał czekać po regeneracji
    db.execute("DELETE FROM totp_lockout")

    uri = pyotp.TOTP(new_secret).provisioning_uri(name="rodzic", issuer_name=TOTP_ISSUER)
    log_event(db, "totp_regenerated", {"via": "admin_ui"})

    return {
        "ok": True,
        "secret": new_secret,
        "uri": uri,
        "qr_png_base64": _png_qr_base64(uri),
    }


# === POST /admin/api/change_password ===


class PasswordBody(_Body):
    current: str
    new: Annotated[str, Field(min_length=8)]


@router.post("/change_password")
async def change_password(body: PasswordBody, db: sqlite3.Connection = Depends(get_db)) -> dict:
    """Existing sessions stay valid (admin_session_key is separate from the password)."""
    if not verify_admin_password(db, body.current):
        log_event(db, "admin_password_change_failed")
        raise HTTPException(status_code=401, detail="wrong_current_password")
    set_config_value(db, "parent_password_hash", hash_password(body.new))
    log_event(db, "admin_password_changed")
    return {"ok": True}
