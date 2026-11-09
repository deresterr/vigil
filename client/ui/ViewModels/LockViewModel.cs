using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Vigil.Ui.ViewModels;

/// <summary>
/// The lock screen's states:
///
///   Initial          → Locked (message and controls from UiState)
///   Locked + code    → GrantPending (sent to the watchdog, waiting)
///   GrantPending OK  → GrantApplied ("Added X minutes")
///   GrantPending err → Locked (with the error)
///
/// After GrantApplied the watchdog sees the unlock at its next tick and removes the overlay.
/// </summary>
public sealed class LockViewModel : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    private LockUiPhase _phase = LockUiPhase.Locked;
    public LockUiPhase Phase
    {
        get => _phase;
        set
        {
            if (value == LockUiPhase.GrantApplied && _phase != value) GrantAppliedAtUtc = DateTime.UtcNow;
            SetField(ref _phase, value);
        }
    }

    /// <summary>When the current grant message appeared (App keeps it on screen for a minimum time).</summary>
    public DateTime GrantAppliedAtUtc { get; private set; }

    private UiState _state = new() { Reason = LockReason.Limit };
    public UiState State
    {
        get => _state;
        set
        {
            if (!Equals(_state, value))
            {
                _state = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(MainMessage));
                OnPropertyChanged(nameof(ShowTotpField));
                OnPropertyChanged(nameof(ShowResumeButton));
                OnPropertyChanged(nameof(ShowRetryButton));
                OnPropertyChanged(nameof(MinutesRemainingText));
            }
        }
    }

    public string MainMessage        => Strings.LockMessage(_state.Reason, _state.ShowTotpField);
    public bool ShowTotpField        => _phase == LockUiPhase.Locked && _state.ShowTotpField;
    public bool ShowResumeButton     => _phase == LockUiPhase.Locked && _state.ShowResumeButton;
    public bool ShowRetryButton      => _phase == LockUiPhase.Locked && _state.ShowRetryButton;

    /// <summary>
    /// Time left, shown during a pause; empty when none is left (no "0 min left").
    /// </summary>
    public string MinutesRemainingText =>
        _state.ShowResumeButton && _state.MinutesRemaining > 0
            ? Strings.Remaining(_state.MinutesRemaining)
            : "";

    private string _totpCode = "";
    public string TotpCode
    {
        get => _totpCode;
        set => SetField(ref _totpCode, value);
    }

    private string _statusMessage = "";
    /// <summary>Status line (wrong code, lockout, minutes added).</summary>
    public string StatusMessage
    {
        get => _statusMessage;
        set => SetField(ref _statusMessage, value);
    }

    private string _currentTime = DateTime.Now.ToString("HH:mm");
    public string CurrentTime
    {
        get => _currentTime;
        set => SetField(ref _currentTime, value);
    }

    private void SetField<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (Equals(field, value)) return;
        field = value;
        OnPropertyChanged(name);
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name!));
}

public enum LockUiPhase
{
    Locked,         // message, and the code field / Retry when offered
    GrantPending,   // code sent, waiting for the answer
    GrantApplied,   // success shown until the watchdog unlocks
}
