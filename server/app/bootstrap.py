"""First-run bootstrap: inicjalizuje DB, generuje sekrety techniczne.

Hasło rodzica, TOTP i ustawienia konfigurowane przez kreator /setup przy pierwszym wejściu
na panel. Bootstrap tylko przygotowuje "fundament" — sekrety kryptograficzne i strukturę DB.
"""

from __future__ import annotations

import logging
import sys

import pyotp

from .config import CONFIG_DEFAULTS
from .crypto import generate_token_b64
from .db import get_connection

logger = logging.getLogger(__name__)


def _is_first_run(conn) -> bool:
    """First run = no signing_key in config (secrets not generated yet)."""
    return conn.execute(
        "SELECT 1 FROM config WHERE key = 'signing_key'"
    ).fetchone() is None


def _insert_config_defaults(conn) -> None:
    """Wpisuje brakujące klucze konfiguracyjne (INSERT OR IGNORE — nie nadpisuje istniejących)."""
    conn.executemany(
        "INSERT OR IGNORE INTO config(key, value) VALUES (?, ?)",
        CONFIG_DEFAULTS.items(),
    )


def run_bootstrap() -> None:
    """Wołane przy starcie aplikacji (lifespan). Idempotent."""
    with get_connection() as conn:
        _insert_config_defaults(conn)
        # Protocol v2 dropped the bearer token; remove it from databases created before.
        conn.execute("DELETE FROM config WHERE key = 'bearer_token'")
        # Pairing codes replaced the open init mode.
        conn.execute("DELETE FROM config WHERE key = 'init_open'")

        if not _is_first_run(conn):
            logger.info("Database OK.")
            return

        # First run — generuj sekrety kryptograficzne
        secrets = [
            ("totp_secret",       pyotp.random_base32()),
            ("signing_key",       generate_token_b64(32)),
            ("admin_session_key", generate_token_b64(32)),
        ]
        conn.executemany(
            "INSERT OR REPLACE INTO config(key, value) VALUES (?, ?)",
            secrets,
        )
        conn.execute(
            "INSERT INTO events(event_type, details_json) VALUES (?, ?)",
            ("vigil_started", '{"phase": "bootstrap"}'),
        )

    sys.stdout.flush()
