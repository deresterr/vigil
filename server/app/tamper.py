"""Tamper detection from watchdog telemetry (issue #7). Detection, not protection.

Every heartbeat carries:
    instance_id        random id of the watchdog process (new on every start)
    awake_ms           system time excluding sleep/hibernate (QueryUnbiasedInterruptTime)
    awake_ms_at_start  awake_ms when this watchdog process started
    failed_attempts    heartbeats that failed since the last successful one

"Awake" time is what makes this work: sleep, hibernate and Fast Startup do not advance it, so a long
awake gap means the PC was running while we heard nothing. awake_ms restarts near 0 on every boot.
The other way round, a long gap on the server's clock with a short awake gap means the PC was asleep — or shut
down with Fast Startup, which hibernates the service instead of stopping it: no goodbye, the same process after
the start (#130). Both are logged as a shutdown and a start, so the journal shows when the PC was off.

Events with the "tamper_" prefix are alerts for the parent panel (#8); the rest are informational.
"""

from __future__ import annotations

import sqlite3
from datetime import datetime, timedelta, timezone

from .auth import Device
from .db import log_event

# A live watchdog heartbeats at least every 60 s (inactive session); anything above this awake gap is
# unexplained unless the watchdog says it was retrying.
SILENCE_THRESHOLD_S = 90

# Off (asleep, or shut down with Fast Startup) for longer than this: a shutdown and a start in the journal.
ASLEEP_THRESHOLD_S = 120

GOODBYE_REASONS = ("service_stop", "shutdown")


def process_telemetry(
    db: sqlite3.Connection,
    device: Device,
    instance_id: str,
    awake_ms: int,
    awake_ms_at_start: int,
    failed_attempts: int,
    prev_heartbeat_utc: datetime | None,
) -> None:
    """Compares with the device's previous heartbeat (devices.wd_*; reset when it is re-paired).

    prev_heartbeat_utc: when the server got that heartbeat (devices.last_heartbeat_utc before this one).
    """
    prev = db.execute("SELECT wd_instance_id, wd_awake_ms, wd_goodbye FROM devices WHERE id = ?",
                      (device.id,)).fetchone()
    prev_instance, prev_awake = prev["wd_instance_id"], prev["wd_awake_ms"]
    goodbye_instance, _, goodbye_reason = prev["wd_goodbye"].partition(":")

    def log(event_type: str, details: dict, ts_utc: datetime | None = None) -> None:
        log_event(db, event_type, details, ts_utc, child_id=device.child_id, device_id=device.id)

    if prev_instance and prev_awake >= 0:
        if instance_id != prev_instance:
            # A new process. Same boot = it started after the old one's last heartbeat on the same
            # awake clock; after a reboot the clock restarted and awake_ms_at_start is small.
            same_boot = awake_ms_at_start >= prev_awake
            said_goodbye = goodbye_instance == prev_instance
            if same_boot and not said_goodbye:
                log("tamper_watchdog_restarted", {"down_awake_seconds": (awake_ms_at_start - prev_awake) // 1000})
            else:
                log("watchdog_started", {"same_boot": same_boot, "after": goodbye_reason or None})
        else:
            gap_s = (awake_ms - prev_awake) // 1000
            now = datetime.now(timezone.utc)
            if prev_heartbeat_utc is not None:
                asleep_s = int((now - prev_heartbeat_utc).total_seconds()) - max(gap_s, 0)
                if asleep_s > ASLEEP_THRESHOLD_S:
                    # The awake seconds before the sleep are a tick at most: stamped as if all came after it.
                    woke = prev_heartbeat_utc + timedelta(seconds=asleep_s)
                    log("watchdog_shutdown", {"instance_id": instance_id, "sleep": True}, prev_heartbeat_utc)
                    log("watchdog_started", {"same_boot": False, "after": "sleep",
                                             "asleep_seconds": asleep_s}, woke)
            if gap_s > SILENCE_THRESHOLD_S:
                if failed_attempts == 0:
                    # Alive before and after, never even tried in between: suspended or frozen.
                    log("tamper_watchdog_silent", {"awake_gap_seconds": gap_s})
                else:
                    # Stamped when the outage began (we only hear of it on reconnect), so the journal
                    # shows it before whatever happened during it, e.g. an offline unlock.
                    started = now - timedelta(seconds=gap_s)
                    log("watchdog_offline", {"awake_gap_seconds": gap_s, "failed_attempts": failed_attempts},
                        started)

    db.execute("UPDATE devices SET wd_instance_id = ?, wd_awake_ms = ? WHERE id = ?",
               (instance_id, awake_ms, device.id))


def process_goodbye(db: sqlite3.Connection, device: Device, instance_id: str, reason: str) -> None:
    db.execute("UPDATE devices SET wd_goodbye = ? WHERE id = ?", (f"{instance_id}:{reason}", device.id))
    ids = {"child_id": device.child_id, "device_id": device.id}
    if reason == "service_stop":
        # The service DACL denies stop to admins; only SYSTEM can stop it — Vigil's own installer
        # (uninstall/update-config) or someone who bypassed the DACL. Either way the parent sees it.
        log_event(db, "tamper_service_stopped", {"instance_id": instance_id}, **ids)
    else:
        log_event(db, "watchdog_shutdown", {"instance_id": instance_id}, **ids)
