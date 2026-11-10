using System;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Vigil.Ipc;

// Pipe \\.\pipe\VigilUI between vigil_watchdog (server, SYSTEM) and vigil_ui (client, child's session).
// JSON, one message per line, "type" is the discriminator. This one file is compiled into both
// projects (<Compile Include Link>), so the two sides cannot drift apart.
// No sentences cross the pipe (i18n, D21): vigil_ui words reasons and results in the child's language.

// === Watchdog → UI ===

/// <summary>Show the lock overlay (or update it) with this state.</summary>
public sealed record IpcSetState
{
    [JsonPropertyName("type")] public string Type => "set_state";
    [JsonPropertyName("lock_reason")] public string LockReason { get; init; } = "limit";
    [JsonPropertyName("show_totp_field")] public bool ShowTotpField { get; init; }
    [JsonPropertyName("show_resume_button")] public bool ShowResumeButton { get; init; }
    [JsonPropertyName("show_retry_button")] public bool ShowRetryButton { get; init; }
    [JsonPropertyName("minutes_remaining")] public int MinutesRemaining { get; init; }
    [JsonPropertyName("current_time_display")] public string? CurrentTimeDisplay { get; init; }
}

/// <summary>Hide the lock overlay; the process keeps running with its tray icon.</summary>
public sealed record IpcUnlock
{
    [JsonPropertyName("type")] public string Type => "unlock";
}

/// <summary>Tray tooltip state, sent after every heartbeat.</summary>
public sealed record IpcTrayState
{
    [JsonPropertyName("type")] public string Type => "tray_state";
    [JsonPropertyName("paused")] public bool Paused { get; init; }
    [JsonPropertyName("can_pause")] public bool CanPause { get; init; }
    [JsonPropertyName("minutes_remaining")] public int MinutesRemaining { get; init; }
}

/// <summary>Quit the UI process (service stopping).</summary>
public sealed record IpcExit
{
    [JsonPropertyName("type")] public string Type => "exit";
}

public sealed record IpcTotpResponse
{
    [JsonPropertyName("type")] public string Type => "totp_response";
    [JsonPropertyName("ok")] public bool Ok { get; init; }
    [JsonPropertyName("mode")] public string? Mode { get; init; }
    [JsonPropertyName("minutes")] public int? Minutes { get; init; }
    /// <summary>extend_night: end of the extension, local "yyyy-MM-dd HH:mm".</summary>
    [JsonPropertyName("until_local")] public string? UntilLocal { get; init; }
    [JsonPropertyName("reason")] public string? Reason { get; init; }
    [JsonPropertyName("lockout_seconds_remaining")] public int? LockoutSecondsRemaining { get; init; }
}

// === UI → Watchdog ===

public sealed record IpcTotpAttempt
{
    [JsonPropertyName("type")] public string Type => "totp_attempt";
    [JsonPropertyName("code")] public string Code { get; init; } = "";
    [JsonPropertyName("minutes")] public int Minutes { get; init; }
}

public sealed record IpcRetryNow
{
    [JsonPropertyName("type")] public string Type => "retry_now";
}

/// <summary>"I'm back" on the pause lock screen.</summary>
public sealed record IpcResumeFromPause
{
    [JsonPropertyName("type")] public string Type => "resume_from_pause";
}

/// <summary>Left click on the tray icon.</summary>
public sealed record IpcPauseToggle
{
    [JsonPropertyName("type")] public string Type => "pause_toggle";
}

public static class IpcSerializer
{
    private static readonly JsonSerializerOptions _opts = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>Single-line JSON (no indentation): the protocol is line-delimited.</summary>
    public static string Serialize(object message) => JsonSerializer.Serialize(message, message.GetType(), _opts);

    /// <summary>Message by its "type", or null for malformed or unknown lines.</summary>
    public static object? TryDeserialize(string line)
    {
        try
        {
            using var doc = JsonDocument.Parse(line);
            if (!doc.RootElement.TryGetProperty("type", out var typeProp))
                return null;
            return typeProp.GetString() switch
            {
                "set_state" => JsonSerializer.Deserialize<IpcSetState>(line, _opts),
                "unlock" => JsonSerializer.Deserialize<IpcUnlock>(line, _opts),
                "tray_state" => JsonSerializer.Deserialize<IpcTrayState>(line, _opts),
                "exit" => JsonSerializer.Deserialize<IpcExit>(line, _opts),
                "totp_response" => JsonSerializer.Deserialize<IpcTotpResponse>(line, _opts),
                "totp_attempt" => JsonSerializer.Deserialize<IpcTotpAttempt>(line, _opts),
                "retry_now" => JsonSerializer.Deserialize<IpcRetryNow>(line, _opts),
                "resume_from_pause" => JsonSerializer.Deserialize<IpcResumeFromPause>(line, _opts),
                "pause_toggle" => JsonSerializer.Deserialize<IpcPauseToggle>(line, _opts),
                _ => null,
            };
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
