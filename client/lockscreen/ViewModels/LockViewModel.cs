using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Vigil.Lockscreen.ViewModels;

/// <summary>
/// State machine lockscreen UI:
///
///   Initial          → Locked (wyświetl komunikat + UI z UiState)
///   Locked + TOTP OK → GrantPending (wysłano do watchdoga, czekamy)
///   GrantPending OK  → GrantApplied (komunikat "Dodano X minut...")
///   GrantPending err → Locked (z error message)
///
/// Po GrantApplied: watchdog zauważy unlock przy następnym tickcie i zamknie lockscreen.
/// </summary>
public sealed class LockViewModel : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    private LockUiPhase _phase = LockUiPhase.Locked;
    public LockUiPhase Phase
    {
        get => _phase;
        set => SetField(ref _phase, value);
    }

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

    public string MainMessage        => _state.MainMessage ?? DefaultMessages.ForReason(_state.Reason);
    public bool ShowTotpField        => _phase == LockUiPhase.Locked && _state.ShowTotpField;
    public bool ShowResumeButton     => _phase == LockUiPhase.Locked && _state.ShowResumeButton;
    public bool ShowRetryButton      => _phase == LockUiPhase.Locked && _state.ShowRetryButton;

    /// <summary>
    /// Czas pozostały do wyświetlenia na ekranie pauzy.
    /// Pusta stringa gdy brak (nie pokazuj gdy limit wyczerpany — nie ma sensu pisać "Pozostało: 0 min").
    /// </summary>
    public string MinutesRemainingText =>
        _state.ShowResumeButton && _state.MinutesRemaining > 0
            ? $"Pozostało: {FormatTime(_state.MinutesRemaining)}"
            : "";

    private string _totpCode = "";
    public string TotpCode
    {
        get => _totpCode;
        set => SetField(ref _totpCode, value);
    }

    private string _statusMessage = "";
    /// <summary>Komunikat statusu (np. "Nieprawidłowy kod", "Lockout 30s", "Dodano 30 minut").</summary>
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

    private static string FormatTime(int minutes)
    {
        if (minutes < 60) return $"{minutes} min";
        int h = minutes / 60, m = minutes % 60;
        return m > 0 ? $"{h} h {m} min" : $"{h} h";
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
    Locked,         // Pokazujemy komunikat + opcjonalnie TOTP/retry
    GrantPending,   // Wysłano do watchdoga, czekamy na response
    GrantApplied,   // Sukces — wyświetl podsumowanie, czekaj aż watchdog zamknie window
}
