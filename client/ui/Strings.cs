using System.Globalization;

namespace Vigil.Ui;

/// <summary>
/// Every text vigil_ui shows (i18n, D21): Polish when the child's Windows display language is Polish,
/// English otherwise. vigil_ui runs in the child's session, so CurrentUICulture is theirs; the watchdog
/// (SYSTEM) and the server send only codes and numbers. XAML binds the fixed texts with {x:Static}.
/// </summary>
public static class Strings
{
    private static readonly bool Pl = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "pl";

    private static string T(string pl, string en) => Pl ? pl : en;

    // === Lock screen, fixed texts ===
    public static string EnterCode => T("Wpisz kod od rodzica:", "Enter the code from your parent:");
    public static string Check => T("Sprawdź", "Check");
    public static string RetryConnection => T("Spróbuj ponownie", "Try again");
    public static string Resume => T("▶  Odblokuj", "▶  Unlock");
    public static string Sending => T("Wysyłanie...", "Sending...");
    public static string UnlockingSoon => T("Komputer odblokuje się za chwilę.", "The computer will unlock in a moment.");
    public static string ShutDown => T("Wyłącz komputer", "Shut down");
    public static string ShutDownConfirm => T("Czy na pewno chcesz wyłączyć komputer?", "Shut down the computer?");
    public static string ShutDownTitle => T("Wyłączanie", "Shut down");

    /// <param name="canUnlockOffline">server_unreachable only: the PC holds the parent's code (TOTP field shown).</param>
    public static string LockMessage(LockReason reason, bool canUnlockOffline) => reason switch
    {
        LockReason.Limit => T("Skończył się Twój czas na dziś. Zapraszam jutro!", "Your time for today is up. See you tomorrow!"),
        LockReason.Night => T("Pora spać. Komputer odblokuje się rano.", "Time to sleep. The computer unlocks in the morning."),
        LockReason.Manual => T("Komputer został zablokowany przez rodzica.", "Your parent has locked the computer."),
        LockReason.Pause => T("Pauza — czas nie leci. Wróć, gdy będziesz gotowy!", "Paused — time is not counted. Come back when you are ready!"),
        LockReason.ServerUnreachable => canUnlockOffline
            ? T("Brak połączenia z serwerem rodzica. Rodzic może odblokować komputer kodem z telefonu.",
                "No connection to your parent's server. Your parent can unlock the computer with the code from their phone.")
            : T("Brak połączenia z serwerem rodzica. Komputer odblokuje się po przywróceniu połączenia.",
                "No connection to your parent's server. The computer unlocks when the connection is back."),
        _ => T("Komputer jest zablokowany.", "The computer is locked."),
    };

    // === Codes and grants ===
    public static string CodeNeedsSixDigits => T("Kod musi mieć 6 cyfr.", "The code has 6 digits.");

    /// <summary>After an accepted code: what it did (server or offline grant).</summary>
    public static string Granted(string? mode, int? minutes, string? untilLocal) => mode switch
    {
        "extend_limit" => T($"Dodano {minutes} minut.", $"{minutes} minutes added."),
        "extend_night" when untilLocal is { Length: >= 5 } u =>
            T($"Możesz korzystać z komputera do {u[^5..]}.", $"You can use the computer until {u[^5..]}."),
        "extend_night" => T($"Dodano {minutes} minut.", $"{minutes} minutes added."),
        "manual_unlock" => T("Rodzic zdjął blokadę.", "Your parent removed the lock."),
        "offline_unlock" => T($"Odblokowano na {minutes} min (bez połączenia z serwerem).",
                              $"Unlocked for {minutes} min (without a server connection)."),
        _ => T("Kod przyjęty.", "Code accepted."),
    };

    public static string CodeRejected(string? reason, int? lockoutSeconds) => reason switch
    {
        "invalid_code" => T("Nieprawidłowy kod. Spróbuj ponownie.", "Wrong code. Try again."),
        "offline_not_configured" => T("Odblokowanie bez serwera jeszcze niedostępne — komputer nie pobrał kodu rodzica.",
                                      "Unlocking without the server is not available yet — this computer has not received the parent's code."),
        "network_error" => T("Brak połączenia z serwerem. Spróbuj ponownie za chwilę.", "No connection to the server. Try again in a moment."),
        "lockout" => T($"Zbyt wiele prób. Poczekaj {lockoutSeconds ?? 0} s.", $"Too many attempts. Wait {lockoutSeconds ?? 0} s."),
        _ => Error(reason ?? T("nieznany", "unknown")),
    };

    // === Status line ===
    public static string Reconnecting => T("Ponawianie połączenia...", "Reconnecting...");
    public static string OneMoment => T("Chwileczkę...", "One moment...");
    public static string PauseEndedTest => T("Pauza zakończona (test).", "Pause ended (test).");
    public static string NoWatchdog => T("Brak połączenia z watchdogiem.", "No connection to the Vigil service.");
    public static string WatchdogLost => T("Utracono połączenie z watchdogiem.", "Lost the connection to the Vigil service.");
    public static string WatchdogTimeout => T("Usługa Vigil nie odpowiedziała.", "The Vigil service did not answer.");
    public static string Error(string detail) => T($"Błąd: {detail}", $"Error: {detail}");
    public static string ShutDownFailed(string detail) => T($"Nie udało się wyłączyć: {detail}", $"Could not shut down: {detail}");

    // === Time left (pause screen, tray) ===
    public static string Remaining(int minutes) => T($"Pozostało: {Duration(minutes)}", $"Remaining: {Duration(minutes)}");
    public static string TrayLoading => T("Ładowanie...", "Loading...");
    public static string TrayActive => T("Komputer aktywny", "Computer active");
    public static string TrayDisconnected => T("Vigil: brak połączenia", "Vigil: no connection");

    public static string Duration(int minutes)
    {
        if (minutes < 60) return $"{minutes} min";
        int h = minutes / 60, m = minutes % 60;
        return m > 0 ? $"{h} h {m} min" : $"{h} h";
    }
}
