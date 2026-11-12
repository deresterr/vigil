using System.Globalization;

namespace Vigil.Ui;

/// <summary>
/// Every text vigil_ui shows (i18n, D21): Polish when the child's Windows display language is Polish,
/// English otherwise. vigil_ui runs in the child's session, so CurrentUICulture is theirs; the watchdog
/// (SYSTEM) and the server send only codes and numbers. XAML binds the fixed texts with {x:Static}.
/// No exception text reaches the child: failures get a sentence here, the details go to ui.log.
/// </summary>
public static class Strings
{
    private static readonly bool Pl = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "pl";

    private static string T(string pl, string en) => Pl ? pl : en;

    // === Lock screen, fixed texts ===
    public static string EnterCode => T("Wpisz kod od rodzica", "Enter the code from your parent");
    public static string Check => T("Sprawdź", "Check");
    public static string RetryConnection => T("Spróbuj ponownie", "Try again");
    public static string Resume => T("Wracam do komputera", "Back to the computer");
    public static string Sending => T("Sprawdzanie kodu…", "Checking the code…");
    public static string UnlockingSoon => T("Komputer odblokuje się za chwilę.", "The computer will unlock in a moment.");
    public static string ShutDown => T("Wyłącz komputer", "Shut down");
    public static string ShutDownConfirm => T("Wyłączyć komputer?", "Shut down the computer?");
    public static string ShutDownYes => T("Wyłącz", "Shut down");
    public static string Cancel => T("Anuluj", "Cancel");

    /// <summary>The big line: what the lock is.</summary>
    public static string LockTitle(LockReason reason) => reason switch
    {
        LockReason.Limit => T("Czas na dziś się skończył", "Your time for today is up"),
        LockReason.Night => T("Pora spać", "Time to sleep"),
        LockReason.Manual => T("Rodzic zablokował komputer", "Your parent locked the computer"),
        LockReason.Pause => T("Pauza — czas nie leci", "Paused — time is not counted"),
        LockReason.ServerUnreachable => T("Brak połączenia z serwerem rodzica", "No connection to your parent's server"),
        LockReason.NetworkDown => T("Komputer nie jest połączony z siecią", "This computer is not connected to a network"),
        LockReason.DeviceUnpaired => T("Ten komputer nie jest połączony z panelem rodzica",
                                       "This computer is not linked to your parent's panel"),
        _ => T("Komputer jest zablokowany", "The computer is locked"),
    };

    /// <summary>The line below: when it ends, or what can be done. Empty when there is nothing to add.</summary>
    public static string LockDetail(UiState s) => s.Reason switch
    {
        LockReason.Limit => s.ShowTotpField
            ? T("Do jutra! Rodzic może dodać czas kodem.", "See you tomorrow! Your parent can add time with a code.")
            : T("Do jutra!", "See you tomorrow!"),
        LockReason.Night => s.NightEndLocal is { Length: > 0 } end
            ? T($"Komputer odblokuje się o {end}.", $"The computer unlocks at {end}.")
            : T("Komputer odblokuje się rano.", "The computer unlocks in the morning."),
        LockReason.Pause => s.MinutesRemaining > 0
            ? T($"Pozostało dziś: {Duration(s.MinutesRemaining)}", $"Left today: {Duration(s.MinutesRemaining)}")
            : "",
        // The TOTP field is shown when the PC holds the parent's code (offline unlock).
        LockReason.ServerUnreachable => s.ShowTotpField
            ? T("Rodzic może odblokować komputer kodem z telefonu.", "Your parent can unlock the computer with the code from their phone.")
            : T("Komputer odblokuje się po przywróceniu połączenia.", "The computer unlocks when the connection is back."),
        // Mostly the seconds before Wi-Fi joins after logon (#128): the lock goes by itself once it does.
        LockReason.NetworkDown => T("Odblokuje się po połączeniu z serwerem rodzica.",
                                    "It unlocks once it reaches your parent's server."),
        LockReason.DeviceUnpaired => s.ShowTotpField
            ? T("Rodzic może go odblokować kodem z telefonu i sparować ponownie.",
                "Your parent can unlock it with the code from their phone and pair it again.")
            : T("Rodzic musi go sparować ponownie.", "Your parent needs to pair it again."),
        _ => "",
    };

    // === Codes and grants ===
    public static string CodeNeedsSixDigits => T("Kod ma 6 cyfr.", "The code has 6 digits.");

    /// <summary>After an accepted code: what it did (server or offline grant).</summary>
    public static string Granted(string? mode, int? minutes, string? untilLocal) => mode switch
    {
        "extend_limit" when minutes > 0 => T($"Dodano {Duration(minutes.Value)}.", $"{Duration(minutes.Value)} added."),
        "extend_night" when untilLocal is { Length: >= 5 } u =>
            T($"Możesz korzystać z komputera do {u[^5..]}.", $"You can use the computer until {u[^5..]}."),
        "extend_night" when minutes > 0 => T($"Dodano {Duration(minutes.Value)}.", $"{Duration(minutes.Value)} added."),
        "manual_unlock" => T("Rodzic zdjął blokadę.", "Your parent removed the lock."),
        "offline_unlock" when minutes > 0 => T($"Odblokowano na {Duration(minutes.Value)} (bez połączenia z serwerem).",
                                               $"Unlocked for {Duration(minutes.Value)} (without a server connection)."),
        _ => T("Kod przyjęty.", "Code accepted."),
    };

    public static string CodeRejected(string? reason, int? lockoutSeconds) => reason switch
    {
        "invalid_code" when lockoutSeconds > 0 => Lockout(lockoutSeconds.Value),
        "invalid_code" => T("Nieprawidłowy kod. Spróbuj ponownie.", "Wrong code. Try again."),
        "lockout" => Lockout(lockoutSeconds ?? 0),
        "offline_not_configured" => T("Odblokowanie bez serwera jeszcze niedostępne — komputer nie pobrał kodu rodzica.",
                                      "Unlocking without the server is not available yet — this computer has not received the parent's code."),
        "network_error" => T("Brak połączenia z serwerem. Spróbuj ponownie za chwilę.", "No connection to the server. Try again in a moment."),
        _ => CodeNotChecked,
    };

    private static string Lockout(int seconds) => T($"Zbyt wiele prób. Poczekaj {Wait(seconds)}.", $"Too many attempts. Wait {Wait(seconds)}.");

    private static string Wait(int seconds) => seconds >= 120 ? $"{(seconds + 59) / 60} min" : $"{seconds} s";

    public static string CodeNotChecked => T("Nie udało się sprawdzić kodu. Spróbuj ponownie.", "Could not check the code. Try again.");

    // === Status line ===
    public static string Reconnecting => T("Ponawianie połączenia…", "Reconnecting…");
    public static string OneMoment => T("Chwileczkę…", "One moment…");
    public static string PauseEndedTest => T("Pauza zakończona (test).", "Pause ended (test).");
    public static string NoService => T("Brak połączenia z usługą Vigil.", "No connection to the Vigil service.");
    public static string ServiceLost => T("Utracono połączenie z usługą Vigil.", "Lost the connection to the Vigil service.");
    public static string ServiceTimeout => T("Usługa Vigil nie odpowiedziała.", "The Vigil service did not answer.");
    public static string ShutDownFailed => T("Nie udało się wyłączyć komputera.", "Could not shut down the computer.");

    // === Tray (NotifyIcon.Text: at most 63 characters) ===
    public static string TrayRemaining(int minutes) => T($"Pozostało: {Duration(minutes)} · kliknij: pauza",
                                                         $"Remaining: {Duration(minutes)} · click to pause");
    public static string TrayNoLimit => T("Bez limitu · kliknij: pauza", "No limit · click to pause");
    public static string TrayLoading => T("Ładowanie…", "Loading…");
    public static string TrayDisconnected => T("Vigil: brak połączenia z usługą", "Vigil: no connection to the service");

    /// <summary>The panel's format: "45 min", "2 h", "1 h 20 min".</summary>
    public static string Duration(int minutes)
    {
        if (minutes < 60) return $"{minutes} min";
        int h = minutes / 60, m = minutes % 60;
        return m > 0 ? $"{h} h {m} min" : $"{h} h";
    }
}
