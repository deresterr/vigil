"""Middleware: HMAC-SHA256 podpis każdej odpowiedzi po pomyślnym auth.

Pokrywa: request_nonce + response_body_bytes (sekcja 5.1).
Header: `X-Signature: <base64>`.

Vigil watchdog weryfikuje przed zaakceptowaniem odpowiedzi (fail-closed gdy mismatch).
Odpowiedzi bez przejścia przez authenticate_request (np. /health, lub błędy auth) NIE są podpisywane —
watchdog traktuje brak podpisu tak samo jak server unreachable.
"""

from __future__ import annotations

import base64
import hashlib
import hmac

from starlette.middleware.base import BaseHTTPMiddleware
from starlette.requests import Request
from starlette.responses import Response


class HmacSigningMiddleware(BaseHTTPMiddleware):
    async def dispatch(self, request: Request, call_next):
        response = await call_next(request)

        nonce = getattr(request.state, "nonce", None)
        signing_key = getattr(request.state, "signing_key_bytes", None)

        if not nonce or not signing_key:
            return response

        # Read body — body_iterator jest "consumable" tylko raz
        chunks = [chunk async for chunk in response.body_iterator]
        body = b"".join(chunks)

        # HMAC: signing_key, message = nonce_bytes + response_body_bytes
        sig = hmac.new(
            signing_key,
            nonce.encode("ascii") + body,
            hashlib.sha256,
        ).digest()
        sig_b64 = base64.b64encode(sig).decode("ascii")

        # Rebuild response — content-length musi być przeliczone, body_iterator został zużyty
        new_headers = dict(response.headers)
        new_headers.pop("content-length", None)
        new_headers["X-Signature"] = sig_b64

        return Response(
            content=body,
            status_code=response.status_code,
            headers=new_headers,
            media_type=response.media_type,
        )
