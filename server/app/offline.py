"""Offline unlock with the parent's TOTP code (#9). Friction + detection, not protection.

When the server is unreachable the watchdog verifies the TOTP code itself, so it needs the secret.
The server hands it over in the (HMAC-signed) heartbeat response whenever the watchdog reports a
different fingerprint — after pairing and after "regenerate TOTP" in the panel, nothing else.

Threat model: the secret sits in totp.dat (DPAPI LocalMachine, SYSTEM-only ACL); an admin child can
extract it and generate codes. Every offline unlock is reported back on reconnect and shown in the
journal, and its TOTP step is burned on the server so the same code cannot be reused online. A grant's step
can only burn up to the current one (+1, the verification window): the request is signed with the device
key, which an admin child can read, and a step far in the future would make every later code a "replay".
"""

from __future__ import annotations

import hashlib
import sqlite3
from datetime import datetime, timezone

import pyotp

from .auth import Device
from .db import get_config_value, log_event, set_config_value


def totp_fingerprint(secret: str) -> str:
    return hashlib.sha256(secret.encode()).hexdigest()[:16]


def sync_offline_totp(db: sqlite3.Connection, device: Device, watchdog_fp: str | None,
                      grants: list[dict]) -> str | None:
    """Record offline grants; return the secret if the watchdog needs it (None otherwise).

    One TOTP secret for all children (D7 decision): every paired watchdog gets the same one.
    A watchdog that does not send totp_fp at all is not offline-capable: never send it the secret.
    """
    secret = get_config_value(db, "totp_secret")
    last_step = int(get_config_value(db, "totp_last_step") or 0)
    max_step = pyotp.TOTP(secret).timecode(datetime.now(timezone.utc)) + 1
    for g in grants:
        # The watchdog resends until a heartbeat succeeds; a lost response must not log it twice.
        # Per device: the same code typed on two PCs while offline is two unlocks.
        seen = db.execute(
            "SELECT 1 FROM events WHERE event_type = 'offline_unlock' AND device_id = ? "
            "AND json_extract(details_json, '$.step') = ?",
            (device.id, g["step"]),
        ).fetchone()
        if not seen:
            try:
                at = datetime.fromisoformat(g["at_utc"].replace("Z", "+00:00"))
            except ValueError:
                at = None
            log_event(db, "offline_unlock", {"minutes": g["minutes"], "step": g["step"]}, ts_utc=at,
                      child_id=device.child_id, device_id=device.id)
        last_step = max(last_step, min(g["step"], max_step))
    if grants:
        set_config_value(db, "totp_last_step", last_step)

    if watchdog_fp is None or watchdog_fp == totp_fingerprint(secret):
        return None
    return secret
