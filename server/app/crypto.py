"""Cryptography: argon2id password hashes, random secrets, TOTP matching."""

from __future__ import annotations

import base64
import hmac
import secrets
from datetime import datetime

import pyotp
from argon2 import PasswordHasher
from argon2.exceptions import InvalidHashError, VerificationError

# The library's argon2id defaults (RFC 9106, second recommended option): one parent, few logins.
_hasher = PasswordHasher()


def hash_password(plain: str) -> str:
    """The parent's password hash (argon2id, encoded with its parameters)."""
    return _hasher.hash(plain)


def verify_password(plain: str, encoded_hash: str) -> bool:
    """True if the password matches (constant time inside the library). A damaged hash refuses the password
    instead of failing the request."""
    try:
        _hasher.verify(encoded_hash, plain)
        return True
    except (VerificationError, InvalidHashError):
        return False


def generate_token_b64(nbytes: int = 32) -> str:
    """N random bytes as base64url without padding."""
    return base64.urlsafe_b64encode(secrets.token_bytes(nbytes)).rstrip(b"=").decode("ascii")


def match_totp_step(secret: str, code: str, now: datetime, window: int = 1) -> int | None:
    """Return the TOTP time step the code belongs to (now ± window steps), or None.

    Callers store the returned step and reject any code whose step is not newer than the last
    accepted one, so a code cannot be used twice inside its ~90 s validity window.
    """
    totp = pyotp.TOTP(secret)
    current = totp.timecode(now)
    for offset in range(-window, window + 1):
        if hmac.compare_digest(totp.at(now, counter_offset=offset), code):
            return current + offset
    return None
