"""Request bodies of the /v1 watchdog protocol (Pydantic, strict: no "1" for 1, no 1 for true).

These are a contract with vigil_watchdog (client/watchdog/Server/HeartbeatModels.cs): unknown fields
are ignored so a newer watchdog still talks to an older server, and a field may only become optional.
Panel request bodies live next to their endpoints (endpoints/admin_api.py).
"""

from __future__ import annotations

from typing import Annotated, Literal

from pydantic import AwareDatetime, BaseModel, ConfigDict, Field, model_validator

NonNegInt = Annotated[int, Field(ge=0)]
InstanceId = Annotated[str, Field(min_length=1, max_length=64)]


class _Wire(BaseModel):
    model_config = ConfigDict(strict=True, extra="ignore")


class OfflineGrant(_Wire):
    """An unlock the watchdog granted itself while the server was unreachable (#9)."""

    at_utc: str
    minutes: int
    step: int


class HeartbeatRequest(_Wire):
    session_active: bool
    user_active: bool
    current_time_utc: str
    uptime_seconds: int
    # Tamper telemetry (#7): all four or none.
    instance_id: InstanceId | None = None
    awake_ms: NonNegInt | None = None
    awake_ms_at_start: NonNegInt | None = None
    failed_attempts: NonNegInt | None = None
    # Offline TOTP (#9): which secret the watchdog holds, unlocks it did on its own.
    totp_fp: Annotated[str, Field(max_length=64)] | None = None
    offline_grants: Annotated[list[OfflineGrant], Field(max_length=50)] = []

    @model_validator(mode="after")
    def _telemetry_all_or_nothing(self) -> HeartbeatRequest:
        given = [v is not None for v in (self.instance_id, self.awake_ms, self.awake_ms_at_start,
                                          self.failed_attempts)]
        if any(given) and not all(given):
            raise ValueError("incomplete_telemetry")
        return self

    @property
    def has_telemetry(self) -> bool:
        return self.instance_id is not None


class GoodbyeRequest(_Wire):
    instance_id: InstanceId
    reason: Literal["service_stop", "shutdown"]


class TotpAttemptRequest(_Wire):
    code: Annotated[str, Field(pattern=r"^\d{6}$")]
    minutes_to_grant: Annotated[int, Field(gt=0, le=1440)] = 60


GuardianCheck = Literal["service_not_running", "service_disabled", "binary_replaced", "ifeo_debugger",
                        "safeboot_missing", "recovery_missing"]


class LockscreenKilled(_Wire):
    type: Literal["lockscreen_killed"]
    kills: NonNegInt | None = None
    window_seconds: NonNegInt | None = None
    logged_off: bool | None = None


class GuardianRepaired(_Wire):
    """One finding of the guardian (#36); the watchdog maps its Event Log IDs to `check`."""

    type: Literal["guardian_repaired"]
    check: GuardianCheck
    repaired: bool
    record_id: NonNegInt
    # FastAPI validates the parsed JSON in Python mode, where strict refuses a string for a datetime.
    occurred_utc: Annotated[AwareDatetime, Field(strict=False)]


TamperEvent = Annotated[LockscreenKilled | GuardianRepaired, Field(discriminator="type")]


class PairRequest(BaseModel):
    """Unauthenticated (pairing.py). Lax: the installer's ConvertTo-Json may send the code as a number."""

    code: str | int = ""
