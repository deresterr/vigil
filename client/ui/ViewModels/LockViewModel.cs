using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media;

namespace Vigil.Ui.ViewModels;

/// <summary>
/// The lock screen's states:
///
///   Locked              — badge, title and detail from UiState; the code field / Retry / Back when offered
///   Locked + code       → GrantPending (sent to the watchdog, waiting)
///   GrantPending OK     → GrantApplied ("1 h added") until the watchdog unlocks
///   GrantPending error  → Locked (with the error)
///   Locked + Shut down  → ConfirmShutdown (on the overlay: a system message box fought the topmost windows)
///   ConfirmShutdown     → Locked (Cancel, Esc) or the power-off
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
            if (Equals(_state, value)) return;
            _state = value;
            OnPropertyChanged();
            foreach (var name in new[] { nameof(Title), nameof(Detail), nameof(StateBrush), nameof(StateIcon),
                         nameof(IsNight), nameof(ShowTotpField), nameof(ShowResumeButton), nameof(ShowRetryButton) })
                OnPropertyChanged(name);
        }
    }

    public string Title => Strings.LockTitle(_state.Reason);
    public string Detail => Strings.LockDetail(_state);
    public System.Windows.Media.Brush StateBrush => Palette.ForState(_state.Reason);
    public Geometry StateIcon => Icons.ForState(_state.Reason);
    /// <summary>Night: the text dims further (a dark room).</summary>
    public bool IsNight => _state.Reason == LockReason.Night;

    public bool ShowTotpField => _state.ShowTotpField;
    public bool ShowResumeButton => _state.ShowResumeButton;
    public bool ShowRetryButton => _state.ShowRetryButton;

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
    /// <summary>The server's time (watchdog, every second while locked); the PC clock only without a watchdog.</summary>
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
    Locked,           // badge and texts, and the code field / Retry / Back when offered
    GrantPending,     // code sent, waiting for the answer
    GrantApplied,     // success shown until the watchdog unlocks
    ConfirmShutdown,  // "Shut down the computer?" on the overlay
}
