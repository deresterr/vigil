"""Middleware: HMAC-SHA256 signature of every response to an authenticated request.

Signed: the request's nonce + the response body. Header `X-Signature: <base64>`.

The watchdog checks it before trusting a response. Responses that did not pass authenticate_request
(/health, authentication errors) are not signed, and the watchdog treats them like an unreachable server.
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

        # The body iterator can be consumed once
        chunks = [chunk async for chunk in response.body_iterator]
        body = b"".join(chunks)

        # HMAC with signing_key over nonce + body
        sig = hmac.new(
            signing_key,
            nonce.encode("ascii") + body,
            hashlib.sha256,
        ).digest()
        sig_b64 = base64.b64encode(sig).decode("ascii")

        # A new response: the iterator is spent and content-length is recomputed
        new_headers = dict(response.headers)
        new_headers.pop("content-length", None)
        new_headers["X-Signature"] = sig_b64

        return Response(
            content=body,
            status_code=response.status_code,
            headers=new_headers,
            media_type=response.media_type,
        )
