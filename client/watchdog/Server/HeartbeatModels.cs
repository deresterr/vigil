using System.Text.Json.Serialization;

namespace Vigil.Watchdog.Server;

/// <summary>
/// Request body dla POST /v1/heartbeat. Format: snake_case zgodny z FastAPI.
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
}

/// <summary>Response z POST /v1/heartbeat.</summary>
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
}

public sealed class UiState
{
    [JsonPropertyName("main_message")]
    public string? MainMessage { get; init; }

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

/// <summary>POST /v1/totp_attempt response. Sukces (200) lub 401/429.</summary>
public sealed class TotpAttemptResponse
{
    [JsonPropertyName("ok")]
    public bool Ok { get; init; }

    [JsonPropertyName("mode")]
    public string? Mode { get; init; }  // extend_limit | extend_night | manual_unlock | noop

    [JsonPropertyName("applied")]
    public string? Applied { get; init; }

    [JsonPropertyName("reason")]
    public string? Reason { get; init; }  // invalid_code | lockout

    [JsonPropertyName("lockout_seconds_remaining")]
    public int? LockoutSecondsRemaining { get; init; }
}
