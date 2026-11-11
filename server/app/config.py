"""Configuration: environment variables (infrastructure only) and the defaults of the `config` table."""

from __future__ import annotations

import os
from pathlib import Path

# === Environment: infrastructure only. Passwords, limits and the time zone come from the wizard. ===
# (LISTEN_PORT is read by the Dockerfile's uvicorn command, not here.)

DATABASE_PATH: Path = Path(os.environ.get("DATABASE_PATH", "/data/vigil.db"))

# === Defaults of the `config` table, inserted when missing; the database values win afterwards. ===

CONFIG_DEFAULTS: dict[str, str] = {
    # Set by the wizard
    "setup_complete":        "0",
    "timezone":              "UTC",  # the wizard's default too (setup.py)
    # Technical parameters — not in the wizard
    # heartbeat_grace_seconds: admin UI only ("online"); the watchdog has its own hard-coded grace (30 s)
    "heartbeat_grace_seconds":    "30",
    "heartbeat_delta_max":        "15",
    "gap_detection_threshold":    "30",
    "nonce_cache_seconds":        "120",
    "max_request_age_seconds":    "60",
    "events_retention_days":      "90",
    # One-time pairing code (endpoints/pairing.py). Only the hash is stored. The code either adds a new
    # device (pairing_child_id + pairing_device_name) or re-pairs an existing one (pairing_device_id).
    "pairing_code_hash":          "",
    "pairing_expires_utc":        "",
    "pairing_attempts":           "0",
    "pairing_child_id":           "",
    "pairing_device_id":          "",
    "pairing_device_name":        "",
    # Last accepted TOTP time step (replay protection; one TOTP secret for all children). Not editable.
    "totp_last_step":             "0",
    # Panel sessions: bumped by a logout or a password change, which revokes every session (admin_auth.py).
    "admin_session_generation":   "0",
}

# Settings of a new child (setup wizard, "Add child" in the panel).
CHILD_DEFAULTS: dict[str, object] = {
    "daily_limit_weekday": 120,
    "daily_limit_weekend": 240,
    "night_lock_start":    "21:30",
    "night_lock_end":      "06:30",
}


def week_schedule(limit_mon_thu: int, limit_fri_sun: int, night_start: str, night_end: str) -> list[dict]:
    """Seven days (Monday first) for `child_schedule`: one limit Mon–Thu, another Fri–Sun, one night window.

    The shape of a new child's week (default_schedule); the wizard and the panel then set each day."""
    return [{"daily_limit": limit_mon_thu if weekday < 4 else limit_fri_sun,
             "night_start": night_start, "night_end": night_end} for weekday in range(7)]


def default_schedule() -> list[dict]:
    """A new child's week: a child added in the panel, the wizard's starting grid and the "Default week"
    template of both (pages.py hands it to schedule.js)."""
    return week_schedule(int(CHILD_DEFAULTS["daily_limit_weekday"]), int(CHILD_DEFAULTS["daily_limit_weekend"]),
                         str(CHILD_DEFAULTS["night_lock_start"]), str(CHILD_DEFAULTS["night_lock_end"]))


# === Constants not stored in the database ===

# The entry in the parent's authenticator app: "Vigil (parent)". A rename only affects new scans.
TOTP_ISSUER: str = "Vigil"
TOTP_ACCOUNT: str = "parent"
