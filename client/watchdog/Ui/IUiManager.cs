namespace Vigil.Watchdog.Ui;

/// <summary>
/// The vigil_ui process in the child's session (D8): tray icon all the time, lock overlay on demand.
/// Real implementation: UiManager.
/// </summary>
public interface IUiManager
{
    /// <summary>
    /// Makes sure vigil_ui runs and is connected in the active session (spawns it if needed).
    /// Returns false when there is no session to run in or the spawn failed. Idempotent, cheap when running.
    /// </summary>
    Task<bool> EnsureRunningAsync(CancellationToken ct);

    /// <summary>Shows (or updates) the lock overlay. Idempotent.</summary>
    Task EnsureLockedAsync(LockState state, CancellationToken ct);

    /// <summary>Hides the lock overlay; vigil_ui keeps running. Idempotent.</summary>
    Task DismissAsync(CancellationToken ct);

    /// <summary>Tray tooltip state (sent after every heartbeat, re-sent after a respawn).</summary>
    Task SendTrayStateAsync(bool paused, bool canPause, int minutesRemaining, CancellationToken ct);

    /// <summary>True while we want the overlay shown (set by EnsureLocked, cleared by Dismiss) and a user's
    /// session is there to show it in.</summary>
    bool IsLocked { get; }

    /// <summary>TOTP code typed on the overlay. Handler → server (or offline check) → result to the UI.</summary>
    event Func<TotpFromLockscreen, CancellationToken, Task<TotpResult>> OnTotpAttempt;

    /// <summary>The Retry button on the server_unreachable overlay.</summary>
    event Func<CancellationToken, Task> OnRetryRequest;

    /// <summary>The "I'm back" button on the pause overlay.</summary>
    event Func<CancellationToken, Task> OnResumeFromPause;

    /// <summary>Left click on the tray icon.</summary>
    event Func<CancellationToken, Task> OnPauseToggle;
}

public sealed record LockState(
    string Reason,
    bool ShowTotpField,
    bool ShowResumeButton,
    bool ShowRetryButton,
    int MinutesRemaining,
    string? CurrentTimeDisplay);

public sealed record TotpFromLockscreen(string Code, int MinutesToGrant);
/// <summary>Codes and numbers only; vigil_ui words them (i18n, D21).</summary>
public sealed record TotpResult(bool Ok, string? Mode = null, int? Minutes = null, string? UntilLocal = null,
                                string? Reason = null, int? LockoutSecondsRemaining = null);
