using System.Globalization;

namespace Vigil.Setup;

/// <summary>
/// Every text vigil_setup shows (i18n, D21): Polish when Windows' display language is Polish, English
/// otherwise. The installer runs elevated in the child's account (D12), so it speaks the language of the
/// lock screen. Technical names (DPAPI, SDDL, file names) stay as they are in both.
/// </summary>
public static class Strings
{
    private static bool Pl => CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "pl";

    private static string T(string pl, string en) => Pl ? pl : en;

    private static string List(IEnumerable<string?> items) => string.Join(", ", items);

    // === General ===

    public static string Usage => T(
        """
        Vigil — instalator

          vigil_setup.exe -Mode verify [-ChildUsername <konto>]
              Sprawdza instalację (albo, gdy Vigila nie ma, czy deinstalacja nic nie zostawiła).
              Kod wyjścia 1 przy błędzie. Niczego nie zmienia.

        Instalacja, deinstalacja i zmiana konfiguracji: na razie Install.cmd.
        """,
        """
        Vigil — installer

          vigil_setup.exe -Mode verify [-ChildUsername <account>]
              Checks the installation (or, without Vigil, that the uninstall left nothing behind).
              Exit code 1 on a failure. Changes nothing.

        Install, uninstall and configuration changes: Install.cmd for now.
        """);

    public static string UnknownParameter(string name) => T($"Nieznany parametr: {name}", $"Unknown parameter: {name}");
    public static string MissingValue(string name) => T($"Parametr {name} wymaga wartości", $"Parameter {name} needs a value");
    public static string UnknownMode(string mode) => T($"Nieznany tryb: {mode}", $"Unknown mode: {mode}");
    public static string ModeNotYet(string mode) => T(
        $"Tryb {mode} jeszcze nie jest w vigil_setup — użyj Install.cmd -Mode {mode}.",
        $"Mode {mode} is not in vigil_setup yet — use Install.cmd -Mode {mode}.");
    public static string PressAnyKey => T("Naciśnij dowolny klawisz, aby zamknąć…", "Press any key to close…");

    // === Accounts ===

    public static string AccountNotFound(string name) => T(
        $"Konto '{name}' nie istnieje albo nie ma profilu na tym komputerze (zaloguj się na nie raz).",
        $"Account '{name}' does not exist or has no profile on this computer (sign in to it once).");
    public static string NoUserAccount => T("Nie znaleziono konta użytkownika z profilem", "No user account with a profile found");
    public static string SeveralAccounts => T(
        "Na komputerze jest kilka kont, a nie podano -ChildUsername",
        "There are several accounts on this computer and no -ChildUsername was given");
    public static string ChildAccount(string name) => T($"Konto dziecka: {name}", $"Child's account: {name}");
    public static string AccountLabel(string name) => T($"Konto {name}", $"Account {name}");
    public static string DefaultProfile => T("Default profile", "Default profile");

    // === Verify ===

    public static string VerifyTitle => T("SPRAWDZENIE", "VERIFY");
    public static string VerifySummary(int pass, int fail, int warn) =>
        T($"{pass} OK, {fail} błędów, {warn} ostrzeżeń", $"{pass} OK, {fail} failed, {warn} warnings");
    public static string CheckCrashed(string message) => T($"Błąd sprawdzania: {message}", $"Check error: {message}");

    public static string SectionService(string name) => T($"Usługa {name}", $"Service {name}");
    public static string SectionFiles(string dir) => T($"Pliki w {dir}", $"Files in {dir}");
    public static string SectionTasks => T("Zadania i rozruch", "Tasks and boot");
    public static string SectionPolicies => T("Policy użytkownika", "User policies");
    public static string SectionRuntime => T("Działanie", "Runtime");
    public static string SectionRemoved => T(
        "Vigil nie jest zainstalowany — sprawdzam, czy nic nie zostało",
        "Vigil is not installed — checking that nothing was left behind");

    public static string ServiceRegistered => T("Zarejestrowana", "Registered");
    public static string ServiceStatus(string status) => T($"Stan: {status}", $"Status: {status}");
    public static string ServiceStart(string start) => T($"Start: {start}", $"Start: {start}");
    public static string ServicePath(string path, bool unquoted) => T(
        $"Ścieżka: {path}" + (unquoted ? " (bez cudzysłowów — zainstalowane przed #47, bez spacji nieszkodliwe)" : ""),
        $"Path: {path}" + (unquoted ? " (unquoted — installed before #47, harmless without a space)" : ""));
    public static string ServiceRecovery(bool nonCrash) => T(
        "Recovery: restart×3" + (nonCrash ? "" : ", bez FailureActionsOnNonCrashFailures"),
        "Recovery: restart×3" + (nonCrash ? "" : ", without FailureActionsOnNonCrashFailures"));
    public static string ServiceDacl(string? ba) => T(
        "DACL: Admin bez stop i delete" + (ba is null ? " — brak wpisu BA" : ba.Contains("WP") || ba.Contains("SD") ? $" — BA={ba}" : ""),
        "DACL: Admin without stop and delete" + (ba is null ? " — no BA entry" : ba.Contains("WP") || ba.Contains("SD") ? $" — BA={ba}" : ""));

    public static string FilesComplete(IReadOnlyCollection<string?> missing) => T(
        "Komplet plików" + (missing.Count > 0 ? $" — brak: {List(missing)}" : ""),
        "All files present" + (missing.Count > 0 ? $" — missing: {List(missing)}" : ""));
    public static string NoLegacyFiles(IReadOnlyCollection<string> legacy) => T(
        "Brak pozostałości starszych wersji" + (legacy.Count > 0 ? $": {List(legacy)}" : ""),
        "No leftovers of older versions" + (legacy.Count > 0 ? $": {List(legacy)}" : ""));
    public static string BackupMatches => T("backup\\vigil_watchdog.exe = vigil_watchdog.exe", "backup\\vigil_watchdog.exe = vigil_watchdog.exe");
    public static string GuardianHashMatches => T("guardian.dat: hash zgodny z vigil_watchdog.exe", "guardian.dat: hash matches vigil_watchdog.exe");
    public static string DoesNotDecrypt(string file, string error) => T($"{file} nie odszyfrowuje się: {error}", $"{file} does not decrypt: {error}");
    public static string PackageMatches(string dir) => T($"Zainstalowane binarki = paczka w {dir}", $"Installed binaries = package in {dir}");
    public static string SigningKeyDecrypted(int bytes) => T($"signing.dat: klucz odszyfrowany ({bytes} B)", $"signing.dat: key decrypted ({bytes} B)");
    public static string ConfigServerUrl(string? url) => T($"vigil.toml: server_url = {url}", $"vigil.toml: server_url = {url}");
    public static string ConfigNoBearer => T("vigil.toml: bez bearer_token (protokół v1)", "vigil.toml: no bearer_token (protocol v1)");
    public static string FolderAcl(string? diff) => T(
        "ACL katalogu (SYSTEM full, Admin modify, AuthUsers r+x)" + (diff is null ? "" : $" — {diff}"),
        "Folder ACL (SYSTEM full, Admin modify, AuthUsers r+x)" + (diff is null ? "" : $" — {diff}"));
    public static string FilesAcl(IReadOnlyCollection<string> wrong) => T(
        "ACL plików: dziedziczone, sekrety tylko SYSTEM + odczyt Admin" + (wrong.Count > 0 ? $" — {List(wrong)}" : ""),
        "File ACLs: inherited, secrets SYSTEM only + Admin read" + (wrong.Count > 0 ? $" — {List(wrong)}" : ""));

    public static string TaskExists(string name) => T($"Zadanie '{name}'", $"Task '{name}'");
    public static string TaskAsSystem => T("  jako SYSTEM, najwyższe uprawnienia", "  as SYSTEM, highest privileges");
    public static string TaskEnabled(bool enabled) => T($"  stan: {(enabled ? "włączone" : "wyłączone")}", $"  state: {(enabled ? "enabled" : "disabled")}");
    public static string TaskTriggers => T("  wyzwalacze: przy starcie + co 5 min", "  triggers: at startup + every 5 min");
    public static string TaskRuns(string what) => T($"  uruchamia {what}", $"  runs {what}");
    public static string TaskNotRunYet => T("  jeszcze nie uruchomione (pierwsze uruchomienie w ciągu 5 min)", "  not run yet (first run within 5 min)");
    public static string TaskLastResult(int result, string time) => T($"  ostatni wynik: 0x{result:X} ({time})", $"  last result: 0x{result:X} ({time})");
    public static string NoLegacyTrayTask(string name) => T($"Brak starego zadania '{name}'", $"No old task '{name}'");
    public static string NoTempTasks(IReadOnlyCollection<string> left) => T(
        "Brak tymczasowych zadań SYSTEM" + (left.Count > 0 ? $": {List(left)}" : ""),
        "No temporary SYSTEM tasks" + (left.Count > 0 ? $": {List(left)}" : ""));
    public static string SafeBoot(string mode) => $"SafeBoot {mode}";
    public static string BootMenu(string policy) => T($"Menu F8 wyłączone (bootmenupolicy {policy})", $"F8 menu disabled (bootmenupolicy {policy})");

    public static string NoChildAccountForVerify(string? problem) => T(
        $"Nie ustalono konta dziecka ({problem}) — sprawdzam tylko Default profile",
        $"Child's account not determined ({problem}) — checking the Default profile only");
    public static string HiveUnreadable(string label) => T($"{label}: nie da się odczytać rejestru", $"{label}: registry cannot be read");
    public static string PoliciesSet(string label, int count) => T($"{label}: policy ustawione ({count} wartości)", $"{label}: policies set ({count} values)");
    public static string PoliciesRemoved(string label) => T($"{label}: policy usunięte", $"{label}: policies removed");

    public static string UiRunning => T("vigil_ui działa w sesji użytkownika", "vigil_ui runs in the user's session");
    public static string NobodyLoggedOn => T("Nikt nie jest zalogowany — vigil_ui nie musi działać", "Nobody is logged on — vigil_ui need not run");
    public static string ServerResponds(string url) => T($"Serwer odpowiada: {url}/health", $"Server responds: {url}/health");
    public static string WatchdogEventLog(int count, string? last) => T(
        $"Event Log watchdoga: {count} ostrzeżeń/błędów w 15 min" + (count > 0 ? $" — ostatni: {last}" : ""),
        $"Watchdog Event Log: {count} warnings/errors in 15 min" + (count > 0 ? $" — newest: {last}" : ""));

    public static string NoService(string name) => T($"Brak usługi {name}", $"No service {name}");
    public static string NoFolder(string dir) => T($"Brak {dir}", $"No {dir}");
    public static string NoTask(string name) => T($"Brak zadania '{name}'", $"No task '{name}'");
    public static string NoSafeBoot(string mode) => T($"Brak SafeBoot {mode}", $"No SafeBoot {mode}");
    public static string NoProcesses(IReadOnlyCollection<string> procs) => T(
        "Brak procesów Vigila" + (procs.Count > 0 ? $": {List(procs)}" : ""),
        "No Vigil processes" + (procs.Count > 0 ? $": {List(procs)}" : ""));
}
