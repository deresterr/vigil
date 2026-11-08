"""Centralna konfiguracja: env vars (infrastruktura) + defaulty w tabeli `config` (logika)."""

from __future__ import annotations

import os
from pathlib import Path

# === Env vars — wyłącznie infrastruktura (Docker/host-level) ===
# Logika aplikacji (hasła, limity, strefy czasowe) konfigurowana przez wizard /setup.

DATABASE_PATH: Path = Path(os.environ.get("DATABASE_PATH", "/data/vigil.db"))
LISTEN_HOST: str = os.environ.get("LISTEN_HOST", "0.0.0.0")
LISTEN_PORT: int = int(os.environ.get("LISTEN_PORT", "8088"))

# === Defaulty wpisywane do tabeli `config` przy first-run ===
# Po inicjalizacji DB nadrzędne są wartości z bazy (modyfikowalne przez wizard i admin UI).

CONFIG_DEFAULTS: dict[str, str] = {
    # Set by the wizard
    "setup_complete":        "0",
    "watchdog_server_url":   "",
    "timezone":              "Europe/Warsaw",
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
}

# Settings of a new child (wizard, "Dodaj dziecko"); each child has its own row in `children`.
CHILD_DEFAULTS: dict[str, object] = {
    "daily_limit_weekday": 120,
    "daily_limit_weekend": 240,
    "night_lock_start":    "21:30",
    "night_lock_end":      "06:30",
}

# === Stałe nieprzechowywane w DB ===

TOTP_ISSUER: str = "Vigil"
