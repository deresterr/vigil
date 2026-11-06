"""Tamper detection from watchdog telemetry (issue #7). Detection, not protection.

Every heartbeat carries:
    instance_id        random id of the watchdog process (new on every start)
    awake_ms           system time excluding sleep/hibernate (QueryUnbiasedInterruptTime)
    awake_ms_at_start  awake_ms when this watchdog process started
    failed_attempts    heartbeats that failed since the last successful one

"Awake" time is what makes this work: sleep, hibernate and Fast Startup do not advance it, so a long
awake gap means the PC was running while we heard nothing. awake_ms restarts near 0 on every boot.

Events with the "tamper_" prefix are alerts for the parent panel (#8); the rest are informational.
"""

from __future__ import annotations

import sqlite3

from .db import get_config_value, log_event, set_config_value

# A live watchdog heartbeats at least every 60 s (inactive session); anything above this awake gap is
# unexplained unless the watchdog says it was retrying.
SILENCE_THRESHOLD_S = 90

GOODBYE_REASONS = ("service_stop", "shutdown")


def process_telemetry(
    db: sqlite3.Connection,
    instance_id: str,
    awake_ms: int,
    awake_ms_at_start: int,
    failed_attempts: int,
) -> None:
    prev_instance = get_config_value(db, "wd_instance_id")
    prev_awake = int(get_config_value(db, "wd_awake_ms") or -1)
    goodbye_instance, _, goodbye_reason = (get_config_value(db, "wd_goodbye") or "").partition(":")

    if prev_instance and prev_awake >= 0:
        if instance_id != prev_instance:
            # A new process. Same boot = it started after the old one's last heartbeat on the same
            # awake clock; after a reboot the clock restarted and awake_ms_at_start is small.
            same_boot = awake_ms_at_start >= prev_awake
            said_goodbye = goodbye_instance == prev_instance
            if same_boot and not said_goodbye:
                log_event(db, "tamper_watchdog_restarted", {
                    "down_awake_seconds": (awake_ms_at_start - prev_awake) // 1000,
                })
            else:
                log_event(db, "watchdog_started", {"same_boot": same_boot, "after": goodbye_reason or None})
        else:
            gap_s = (awake_ms - prev_awake) // 1000
            if gap_s > SILENCE_THRESHOLD_S:
                if failed_attempts == 0:
                    # Alive before and after, never even tried in between: suspended or frozen.
                    log_event(db, "tamper_watchdog_silent", {"awake_gap_seconds": gap_s})
                else:
                    log_event(db, "watchdog_offline", {"awake_gap_seconds": gap_s,
                                                       "failed_attempts": failed_attempts})

    set_config_value(db, "wd_instance_id", instance_id)
    set_config_value(db, "wd_awake_ms", awake_ms)


def process_goodbye(db: sqlite3.Connection, instance_id: str, reason: str) -> None:
    set_config_value(db, "wd_goodbye", f"{instance_id}:{reason}")
    if reason == "service_stop":
        # The service DACL denies stop to admins; only SYSTEM can stop it — Vigil's own installer
        # (uninstall/update-config) or someone who bypassed the DACL. Either way the parent sees it.
        log_event(db, "tamper_service_stopped", {"instance_id": instance_id})
    else:
        log_event(db, "watchdog_shutdown", {"instance_id": instance_id})
