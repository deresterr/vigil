"""The server's clock against NTP (#161): detection only.

Everything time-based (night, limit, day boundary, TOTP — online and, through the PCs' ServerClock, offline) follows
the host's clock. When it is off, TOTP codes stop working everywhere and every PC's `clock_drift` blames the PC. This
module asks an NTP server (plain SNTP, RFC 4330) at start-up and every hour and tells the parent: the card in Settings,
an alert in the Journal when the clock goes more than OFF_LIMIT_SECONDS off, a log line when a state starts and ends.

It never sets or corrects time: setting the clock from the container would need CAP_SYS_TIME and change the host's
clock, and an NTP offset used for decisions would let a forged (unauthenticated) SNTP answer on the LAN move the
night and the limit. A forged answer here gives at most a false alert (D28).
"""

from __future__ import annotations

import asyncio
import ipaddress
import logging
import re
import socket
import struct
import time
from datetime import UTC, datetime
from zoneinfo import ZoneInfo, ZoneInfoNotFoundError

from .db import get_config_values, locked_connection, log_event

logger = logging.getLogger("vigil")

OFF_LIMIT_SECONDS = 60
CHECK_INTERVAL_SECONDS = 3600
TIMEOUT_SECONDS = 3.0
_NTP_EPOCH = 2208988800  # 1900-01-01 → 1970-01-01

# A host name or an IPv4 address (an IPv6 one goes through ipaddress); getaddrinfo does the rest.
HOST_RE = re.compile(r"[A-Za-z0-9][A-Za-z0-9.-]{0,252}")

# The last check, for the Settings card and clock_drift's wording: state is "ok" | "off" | "no_answer" |
# "disabled" | "pending" (before the first check). In memory: a restart checks again at once.
_state: dict = {"state": "pending", "offset_seconds": None, "server": "", "checked_utc": None}


def valid_timezone(v: str) -> str:
    """An IANA name zoneinfo knows (the wizard and Settings)."""
    try:
        ZoneInfo(v)
    except (ZoneInfoNotFoundError, ValueError) as e:
        raise ValueError("invalid") from e
    return v


def valid_ntp_server(v: str) -> str:
    """Empty (the check off), a host name, an IPv4 or an IPv6 address — no port."""
    if not v:
        return v
    if ":" in v:
        try:
            ipaddress.IPv6Address(v)
        except ValueError as e:
            raise ValueError("invalid") from e
    elif not HOST_RE.fullmatch(v):
        raise ValueError("invalid")
    return v


def snapshot() -> dict:
    return dict(_state)


def server_clock_confirmed() -> bool:
    """Whether NTP vouches for the server's clock — then a PC's clock_drift is the PC's fault."""
    return _state["state"] == "ok"


def _ntp_time(raw: bytes) -> float:
    seconds, fraction = struct.unpack("!II", raw)
    return seconds - _NTP_EPOCH + fraction / 2**32


def _ntp_bytes(unix: float) -> bytes:
    seconds = int(unix) + _NTP_EPOCH
    return struct.pack("!II", seconds & 0xFFFFFFFF, int((unix % 1) * 2**32))


def request_packet(t1: float) -> bytes:
    """Client mode (3), version 4, our send time as the transmit timestamp (echoed back as the origin)."""
    return bytes([0x23]) + bytes(39) + _ntp_bytes(t1)


def parse_reply(data: bytes, request: bytes, t1: float, t4: float) -> float | None:
    """The server's offset (seconds; positive = our clock is behind) or None for an answer not to trust: wrong mode,
    unsynchronized (leap 3, stratum 0 = kiss-o'-death, 16+), or not an answer to our request (origin ≠ our transmit)."""
    if len(data) < 48 or data[0] & 0x07 != 4 or data[0] >> 6 == 3 or not 1 <= data[1] <= 15:
        return None
    if data[24:32] != request[40:48]:
        return None
    t2, t3 = _ntp_time(data[32:40]), _ntp_time(data[40:48])
    return ((t2 - t1) + (t3 - t4)) / 2


async def sntp_offset(host: str, port: int = 123, timeout: float = TIMEOUT_SECONDS) -> float | None:
    """One SNTP exchange; None when the server cannot be resolved, does not answer in time or answers wrongly."""
    loop = asyncio.get_running_loop()
    try:
        infos = await asyncio.wait_for(loop.getaddrinfo(host, port, type=socket.SOCK_DGRAM), timeout)
        family, _, _, _, addr = infos[0]
        answer: asyncio.Future = loop.create_future()

        class _Reply(asyncio.DatagramProtocol):
            def datagram_received(self, data: bytes, _addr) -> None:
                if not answer.done():
                    answer.set_result((data, time.time()))

            def error_received(self, exc: Exception) -> None:
                if not answer.done():
                    answer.set_exception(exc)

        transport, _ = await loop.create_datagram_endpoint(_Reply, remote_addr=addr, family=family)
        try:
            t1 = time.time()
            request = request_packet(t1)
            transport.sendto(request)
            data, t4 = await asyncio.wait_for(answer, timeout)
        finally:
            transport.close()
    except (OSError, TimeoutError, IndexError):
        return None
    return parse_reply(data, request, t1, t4)


async def check_clock() -> dict:
    """One check: update the state, log a state's start and end, journal the start of "off"."""
    # Under the lock: after a change (check_soon) the request that saved it has committed by the time we get it.
    async with locked_connection() as conn:
        server = get_config_values(conn, ("ntp_server",))["ntp_server"].strip()  # missing row: the default
    previous = _state["state"]
    offset = await sntp_offset(server) if server else None
    if not server:
        state = "disabled"
    elif offset is None:
        state = "no_answer"
    else:
        state = "off" if abs(offset) > OFF_LIMIT_SECONDS else "ok"
    _state.update(state=state, offset_seconds=None if offset is None else round(offset), server=server,
                  checked_utc=datetime.now(UTC).isoformat())

    if state != previous:
        if previous == "off":
            logger.info("Server clock in sync with NTP again")
        elif previous == "no_answer":
            logger.info("NTP server %s answers again", _state["server"] or "")
        if state == "off":
            # Positive offset: NTP is ahead, so the server's clock is slow.
            behind = "slow" if offset > 0 else "fast"
            logger.warning("Server clock is %d s %s against NTP (%s): set up time sync on the host",
                           abs(round(offset)), behind, server)
            async with locked_connection() as conn:
                log_event(conn, "server_clock_off", {"offset_seconds": round(offset), "ntp_server": server})
        elif state == "no_answer":
            logger.warning("NTP server %s does not answer: the server's clock is not checked", server)
    return snapshot()


_pending: set[asyncio.Task] = set()


def check_soon() -> None:
    """A check right after the setting changed. A task, not an await: the request holds get_db's lock until it
    ends, and the check needs it (to read the committed setting, to journal "off")."""
    task = asyncio.get_running_loop().create_task(check_clock())
    _pending.add(task)
    task.add_done_callback(_pending.discard)


async def watch_clock() -> None:
    """The background check: at start-up, then every hour (lifespan)."""
    while True:
        try:
            await check_clock()
        except Exception:  # never let the loop die; the next hour tries again
            logger.exception("Clock check failed")
        await asyncio.sleep(CHECK_INTERVAL_SECONDS)
