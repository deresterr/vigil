"""Startup bootstrap: config defaults and, on the first run, the server's own secrets.

The parent's password, TOTP and limits come from the /setup wizard; this only lays the groundwork.
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
    """First run = no admin_session_key in config (secrets not generated yet)."""
    return conn.execute(
        "SELECT 1 FROM config WHERE key = 'admin_session_key'"
    ).fetchone() is None


def _insert_config_defaults(conn) -> None:
    """Missing config keys get their defaults (INSERT OR IGNORE: existing values stay)."""
    conn.executemany(
        "INSERT OR IGNORE INTO config(key, value) VALUES (?, ?)",
        CONFIG_DEFAULTS.items(),
    )


def run_bootstrap() -> None:
    """Called at startup (lifespan). Idempotent."""
    with get_connection() as conn:
        _insert_config_defaults(conn)

        if not _is_first_run(conn):
            logger.info("Database OK.")
            return

        # First run: server-wide secrets. Device signing keys come from pairing (endpoints/pairing.py).
        secrets = [
            ("totp_secret",       pyotp.random_base32()),
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
