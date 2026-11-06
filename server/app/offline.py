"""Offline unlock with the parent's TOTP code (#9). Friction + detection, not protection.

When the server is unreachable the watchdog verifies the TOTP code itself, so it needs the secret.
The server hands it over in the (HMAC-signed) heartbeat response whenever the watchdog reports a
different fingerprint — after pairing and after "regenerate TOTP" in the panel, nothing else.

Threat model: the secret sits in totp.dat (DPAPI LocalMachine, SYSTEM-only ACL); an admin child can
extract it and generate codes. Every offline unlock is reported back on reconnect and shown in the
journal, and its TOTP step is burned on the server so the same code cannot be reused online.
"""

from __future__ import annotations

import hashlib
import sqlite3
from datetime import datetime

from .db import get_config_value, log_event, set_config_value


def totp_fingerprint(secret: str) -> str:
    return hashlib.sha256(secret.encode()).hexdigest()[:16]


def sync_offline_totp(db: sqlite3.Connection, watchdog_fp: str | None, grants: list[dict]) -> str | None:
    """Record offline grants; return the secret if the watchdog needs it (None otherwise).

    A watchdog that does not send totp_fp at all is not offline-capable: never send it the secret.
    """
    last_step = int(get_config_value(db, "totp_last_step") or 0)
    for g in grants:
        # The watchdog resends until a heartbeat succeeds; a lost response must not log it twice.
        seen = db.execute(
            "SELECT 1 FROM events WHERE event_type = 'offline_unlock' AND json_extract(details_json, '$.step') = ?",
            (g["step"],),
        ).fetchone()
        if not seen:
            try:
                at = datetime.fromisoformat(g["at_utc"].replace("Z", "+00:00"))
            except ValueError:
                at = None
            log_event(db, "offline_unlock", {"minutes": g["minutes"], "step": g["step"]}, ts_utc=at)
        last_step = max(last_step, g["step"])
    if grants:
        set_config_value(db, "totp_last_step", last_step)

    if watchdog_fp is None:
        return None
    secret = get_config_value(db, "totp_secret")
    if not secret or watchdog_fp == totp_fingerprint(secret):
        return None
    return secret
