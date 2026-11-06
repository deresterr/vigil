namespace Vigil.Watchdog.Lockscreen;

/// <summary>
/// Abstrakcja nad spawn lockscreen.exe + named pipe IPC. Real impl: RealLockscreenManager.
/// </summary>
public interface ILockscreenManager
{
    /// <summary>
    /// Idempotentne: jeśli lockscreen już aktywny, tylko aktualizuje state. Inaczej spawnuje
    /// vigil_lockscreen.exe w user session i czeka na pipe connection.
    /// </summary>
    Task EnsureLockedAsync(LockState state, CancellationToken ct);

    /// <summary>Zdejmuje lockscreen jeśli jest aktywny. Idempotentne.</summary>
    Task DismissAsync(CancellationToken ct);

    /// <summary>True jeśli lockscreen jest aktualnie aktywny.</summary>
    bool IsLocked { get; }

    /// <summary>
    /// Subskrypcja na TOTP attempts z lockscreen UI. Manager → handler → server
    /// (podpinane do HeartbeatClient.SendTotpAttemptAsync w Worker).
    /// </summary>
    event Func<TotpFromLockscreen, CancellationToken, Task<TotpResult>> OnTotpAttempt;

    /// <summary>Subskrypcja na "Spróbuj ponownie" gdy lockscreen pokazuje server_unreachable.</summary>
    event Func<CancellationToken, Task> OnRetryRequest;
    /// <summary>Subskrypcja na "Jestem z powrotem" z lockscreen w trybie pauzy.</summary>
    event Func<CancellationToken, Task> OnResumeFromPause;
}

public sealed record LockState(
    string Reason,
    string? MainMessage,
    bool ShowTotpField,
    bool ShowResumeButton,
    bool ShowRetryButton,
    bool ShowShutdownButton,
    int MinutesRemaining,
    string? CurrentTimeDisplay);

public sealed record TotpFromLockscreen(string Code, int MinutesToGrant);
public sealed record TotpResult(bool Ok, string? Mode, string? Applied, string? Reason, int? LockoutSecondsRemaining = null);
