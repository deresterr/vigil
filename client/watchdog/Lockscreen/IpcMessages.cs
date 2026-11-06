using System.Text.Json;
using System.Text.Json.Serialization;

namespace Vigil.Watchdog.Lockscreen;

/// <summary>
/// Pipe protocol — JSON line-delimited messages.
/// Każdy message ma "type" jako discriminator. Bidirectional na tym samym pipe (PipeDirection.InOut).
///
/// IDENTYCZNE klasy są w vigil-lockscreen/IpcMessages.cs — celowy duplikat, bo single-binary
/// publish dla każdego projektu osobno (shared library by wymagała osobnego pakietu).
/// </summary>

// === Watchdog → Lockscreen ===

public sealed record IpcSetState
{
    [JsonPropertyName("type")] public string Type => "set_state";
    [JsonPropertyName("lock_reason")] public string LockReason { get; init; } = "limit";
    [JsonPropertyName("main_message")] public string? MainMessage { get; init; }
    [JsonPropertyName("show_totp_field")] public bool ShowTotpField { get; init; }
    [JsonPropertyName("show_resume_button")] public bool ShowResumeButton { get; init; }
    [JsonPropertyName("show_retry_button")] public bool ShowRetryButton { get; init; }
    [JsonPropertyName("show_shutdown_button")] public bool ShowShutdownButton { get; init; } = true;
    [JsonPropertyName("minutes_remaining")] public int MinutesRemaining { get; init; }
    [JsonPropertyName("current_time_display")] public string? CurrentTimeDisplay { get; init; }
}

public sealed record IpcExit
{
    [JsonPropertyName("type")] public string Type => "exit";
}

public sealed record IpcTotpResponse
{
    [JsonPropertyName("type")] public string Type => "totp_response";
    [JsonPropertyName("ok")] public bool Ok { get; init; }
    [JsonPropertyName("mode")] public string? Mode { get; init; }
    [JsonPropertyName("applied")] public string? Applied { get; init; }
    [JsonPropertyName("reason")] public string? Reason { get; init; }
    [JsonPropertyName("lockout_seconds_remaining")] public int? LockoutSecondsRemaining { get; init; }
}

// === Lockscreen → Watchdog ===

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

public sealed record IpcShutdownRequest
{
    [JsonPropertyName("type")] public string Type => "shutdown_request";
}

// Lockscreen → Watchdog: dziecko kliknęło "Jestem z powrotem" podczas pauzy
public sealed record IpcResumeFromPause
{
    [JsonPropertyName("type")] public string Type => "resume_from_pause";
}

/// <summary>
/// Helpers: serializacja do single-line JSON + dispatcher po `type` field przy odbiorze.
/// </summary>
public static class IpcSerializer
{
    private static readonly JsonSerializerOptions _opts = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        // WriteIndented = false — single line per message, line-delimited protocol
    };

    public static string Serialize(object message) => JsonSerializer.Serialize(message, message.GetType(), _opts);

    /// <summary>
    /// Próbuje rozpoznać typ przez "type" field. Zwraca null dla nieznanego typu.
    /// </summary>
    public static object? TryDeserialize(string line)
    {
        try
        {
            using var doc = JsonDocument.Parse(line);
            if (!doc.RootElement.TryGetProperty("type", out var typeProp))
                return null;
            var typeName = typeProp.GetString();
            return typeName switch
            {
                "set_state" => JsonSerializer.Deserialize<IpcSetState>(line, _opts),
                "exit" => JsonSerializer.Deserialize<IpcExit>(line, _opts),
                "totp_response" => JsonSerializer.Deserialize<IpcTotpResponse>(line, _opts),
                "totp_attempt" => JsonSerializer.Deserialize<IpcTotpAttempt>(line, _opts),
                "retry_now" => JsonSerializer.Deserialize<IpcRetryNow>(line, _opts),
                "shutdown_request" => JsonSerializer.Deserialize<IpcShutdownRequest>(line, _opts),
                "resume_from_pause" => JsonSerializer.Deserialize<IpcResumeFromPause>(line, _opts),
                _ => null,
            };
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
