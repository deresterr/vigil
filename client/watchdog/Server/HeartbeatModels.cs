using System.Text.Json.Serialization;

namespace Vigil.Watchdog.Server;

/// <summary>
/// Request body of POST /v1/heartbeat (snake_case, as the server's Pydantic models).
/// </summary>
public sealed class HeartbeatRequest
{
    [JsonPropertyName("session_active")]
    public required bool SessionActive { get; init; }

    [JsonPropertyName("user_active")]
    public required bool UserActive { get; init; }

    [JsonPropertyName("current_time_utc")]
    public required string CurrentTimeUtc { get; init; }

    [JsonPropertyName("uptime_seconds")]
    public required int UptimeSeconds { get; init; }

    // Tamper telemetry (server/app/tamper.py)
    [JsonPropertyName("instance_id")]
    public required string InstanceId { get; init; }

    [JsonPropertyName("awake_ms")]
    public required long AwakeMs { get; init; }

    [JsonPropertyName("awake_ms_at_start")]
    public required long AwakeMsAtStart { get; init; }

    [JsonPropertyName("failed_attempts")]
    public required int FailedAttempts { get; init; }

    // Offline TOTP (#9, server/app/offline.py)
    [JsonPropertyName("totp_fp")]
    public required string TotpFp { get; init; }

    [JsonPropertyName("offline_grants")]
    public List<OfflineGrantDto>? OfflineGrants { get; init; }
}

public sealed record OfflineGrantDto
{
    [JsonPropertyName("at_utc")]
    public required string AtUtc { get; init; }

    [JsonPropertyName("minutes")]
    public required int Minutes { get; init; }

    [JsonPropertyName("step")]
    public required long Step { get; init; }
}

/// <summary>POST /v1/tamper_event — tampering the watchdog itself observed (#10).</summary>
public sealed class TamperEventRequest
{
    [JsonPropertyName("type")]
    public required string Type { get; init; }  // lockscreen_killed

    [JsonPropertyName("kills")]
    public int Kills { get; init; }

    [JsonPropertyName("window_seconds")]
    public int WindowSeconds { get; init; }

    [JsonPropertyName("logged_off")]
    public bool LoggedOff { get; init; }
}

/// <summary>POST /v1/tamper_event type guardian_repaired — one finding of the guardian (#36).</summary>
public sealed class GuardianRepairRequest
{
    [JsonPropertyName("type")]
    public string Type => "guardian_repaired";

    [JsonPropertyName("check")]
    public required string Check { get; init; }

    [JsonPropertyName("repaired")]
    public bool Repaired { get; init; }

    [JsonPropertyName("record_id")]
    public long RecordId { get; init; }

    [JsonPropertyName("occurred_utc")]
    public required string OccurredUtc { get; init; }
}

/// <summary>POST /v1/goodbye — sent when the service stops.</summary>
public sealed class GoodbyeRequest
{
    [JsonPropertyName("instance_id")]
    public required string InstanceId { get; init; }

    [JsonPropertyName("reason")]
    public required string Reason { get; init; }  // service_stop | shutdown
}

public sealed class GoodbyeResponse
{
    [JsonPropertyName("ok")]
    public bool Ok { get; init; }
}

/// <summary>Response of POST /v1/heartbeat.</summary>
public sealed class HeartbeatResponse
{
    [JsonPropertyName("lock_required")]
    public required bool LockRequired { get; init; }

    [JsonPropertyName("lock_reason")]
    public string? LockReason { get; init; }  // limit | night | manual | null

    [JsonPropertyName("minutes_used")]
    public int MinutesUsed { get; init; }

    [JsonPropertyName("minutes_remaining")]
    public int MinutesRemaining { get; init; }

    [JsonPropertyName("next_check_seconds")]
    public int NextCheckSeconds { get; init; } = 5;

    [JsonPropertyName("server_time_utc")]
    public string? ServerTimeUtc { get; init; }

    [JsonPropertyName("ui_state")]
    public UiState? UiState { get; init; }

    /// <summary>Present only when our totp_fp differs from the server's secret.</summary>
    [JsonPropertyName("totp_secret")]
    public string? TotpSecret { get; init; }
}

public sealed class UiState
{
    [JsonPropertyName("show_totp_field")]
    public bool ShowTotpField { get; init; }

    [JsonPropertyName("show_resume_button")]
    public bool ShowResumeButton { get; init; }

    [JsonPropertyName("show_retry_button")]
    public bool ShowRetryButton { get; init; }

    [JsonPropertyName("show_shutdown_button")]
    public bool ShowShutdownButton { get; init; } = true;

    [JsonPropertyName("current_time_display")]
    public string? CurrentTimeDisplay { get; init; }
}

/// <summary>POST /v1/pause_toggle response.</summary>
public sealed class PauseToggleResponse
{
    [JsonPropertyName("ok")]
    public bool Ok { get; init; }

    [JsonPropertyName("paused")]
    public bool Paused { get; init; }
}

/// <summary>POST /v1/totp_attempt request.</summary>
public sealed class TotpAttemptRequest
{
    [JsonPropertyName("code")]
    public required string Code { get; init; }

    [JsonPropertyName("minutes_to_grant")]
    public required int MinutesToGrant { get; init; }
}

/// <summary>POST /v1/totp_attempt response: 200, or 401/429.</summary>
public sealed class TotpAttemptResponse
{
    [JsonPropertyName("ok")]
    public bool Ok { get; init; }

    [JsonPropertyName("mode")]
    public string? Mode { get; init; }  // extend_limit | extend_night | manual_unlock | noop

    [JsonPropertyName("minutes")]
    public int? Minutes { get; init; }

    [JsonPropertyName("until_local")]
    public string? UntilLocal { get; init; }  // extend_night: "yyyy-MM-dd HH:mm"

    [JsonPropertyName("reason")]
    public string? Reason { get; init; }  // invalid_code | lockout

    [JsonPropertyName("lockout_seconds_remaining")]
    public int? LockoutSecondsRemaining { get; init; }
}
