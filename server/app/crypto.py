"""Kryptografia: argon2id hash hasła + generowanie sekretów."""

from __future__ import annotations

import base64
import secrets

from argon2 import PasswordHasher
from argon2.exceptions import VerifyMismatchError

# Domyślne parametry argon2id z biblioteki są rozsądne (RFC 9106 second recommended).
# Trzymamy domyślne — single-user, low-volume, nie ma sensu nad-tunować.
_hasher = PasswordHasher()


def hash_password(plain: str) -> str:
    """Hash hasła rodzica (argon2id, encoded string z parametrami)."""
    return _hasher.hash(plain)


def verify_password(plain: str, encoded_hash: str) -> bool:
    """True jeśli plain matchuje hash. Constant-time wewnątrz biblioteki."""
    try:
        _hasher.verify(encoded_hash, plain)
        return True
    except VerifyMismatchError:
        return False


def generate_token_b64(nbytes: int = 32) -> str:
    """Generuje N losowych bajtów i zwraca jako base64 (urlsafe, bez paddingu)."""
    return base64.urlsafe_b64encode(secrets.token_bytes(nbytes)).rstrip(b"=").decode("ascii")
