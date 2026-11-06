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
    # Ustawiane przez wizard — puste domyślnie
    "setup_complete":        "0",
    "device_name":           "",
    "watchdog_server_url":  "",
    # Limity i harmonogram — wizard ustawia wartości, tu są fallbacki
    "daily_limit_weekday":   "120",
    "daily_limit_weekend":   "240",
    "night_lock_start":      "21:30",
    "night_lock_end":        "06:30",
    "timezone":              "Europe/Warsaw",
    # Parametry techniczne — niewidoczne w wizard, edytowalne w admin UI
    # heartbeat_grace_seconds: tylko admin UI ("watchdog online"); watchdog ma własny, hardkodowany grace (30s)
    "heartbeat_grace_seconds":    "30",
    "heartbeat_delta_max":        "15",
    "gap_detection_threshold":    "30",
    "nonce_cache_seconds":        "120",
    "max_request_age_seconds":    "60",
    "events_retention_days":      "90",
    # Last watchdog telemetry seen (tamper.py). Not editable.
    "wd_instance_id":             "",
    "wd_awake_ms":                "-1",
    "wd_goodbye":                 "",
    # One-time pairing code (endpoints/pairing.py). Only the hash is stored.
    "pairing_code_hash":          "",
    "pairing_expires_utc":        "",
    "pairing_attempts":           "0",
    # Last accepted TOTP time step (replay protection). Not editable.
    "totp_last_step":             "0",
    # Last lock reason seen by the heartbeat; lock_started events are logged on change. Not editable.
    "last_lock_reason":           "",
}

# === Stałe nieprzechowywane w DB ===

TOTP_ISSUER: str = "Vigil"
