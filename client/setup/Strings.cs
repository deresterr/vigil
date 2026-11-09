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

        Bez parametrów: kreator instalacji albo, gdy Vigil już jest zainstalowany, stan i menu.

        Parametry (skrypty, CI, wsparcie):
          -Mode install       -ServerUrl <adres> -PairingCode <kod> [-ChildUsername <konto>]
                              [-DisableWinRE] [-NoUserPolicies]
          -Mode update        [-ServerUrl <adres>]   pliki z tej paczki, konfiguracja i klucz bez zmian
          -Mode update-config [-ServerUrl <adres>] [-PairingCode <kod>]
          -Mode reinstall     -ServerUrl <adres> -PairingCode <kod>
          -Mode uninstall
          -Mode verify        [-ChildUsername <konto>]   sprawdza, niczego nie zmienia

        Kod wyjścia: 0 — gotowe, 1 — błąd (verify: co najmniej jeden błąd), 2 — złe parametry.
        """,
        """
        Vigil — installer

        Without parameters: the setup wizard or, when Vigil is installed, its status and a menu.

        Parameters (scripts, CI, support):
          -Mode install       -ServerUrl <address> -PairingCode <code> [-ChildUsername <account>]
                              [-DisableWinRE] [-NoUserPolicies]
          -Mode update        [-ServerUrl <address>]   files from this package, configuration and key kept
          -Mode update-config [-ServerUrl <address>] [-PairingCode <code>]
          -Mode reinstall     -ServerUrl <address> -PairingCode <code>
          -Mode uninstall
          -Mode verify        [-ChildUsername <account>]   checks, changes nothing

        Exit code: 0 — done, 1 — failed (verify: at least one failure), 2 — wrong parameters.
        """);

    public static string UnknownParameter(string name) => T($"Nieznany parametr: {name}", $"Unknown parameter: {name}");
    public static string MissingValue(string name) => T($"Parametr {name} wymaga wartości", $"Parameter {name} needs a value");
    public static string UnknownMode(string mode) => T($"Nieznany tryb: {mode}", $"Unknown mode: {mode}");
    public static string PressAnyKey => T("Naciśnij dowolny klawisz, aby zamknąć…", "Press any key to close…");

    // === Parameter mode ===

    public static string Done => T("gotowe", "done");
    public static string Failed => T("BŁĄD", "FAILED");
    public static string LogAt(string path) => T($"Log: {path}", $"Log: {path}");
    public static string UninstallIncomplete => T(
        "nie wszystko zostało usunięte — szczegóły wyżej", "not everything was removed — details above");
    public static string InstallNeedsParams => T(
        "Instalacja z parametrami wymaga -ServerUrl i -PairingCode (bez parametrów uruchamia się kreator).",
        "Installing with parameters needs -ServerUrl and -PairingCode (without parameters the wizard starts).");
    public static string UpdateConfigNeedsParams => T(
        "Podaj co najmniej jeden z: -ServerUrl, -PairingCode.", "Give at least one of: -ServerUrl, -PairingCode.");
    public static string ServerNotResponding(string url) => T(
        $"Serwer {url} nie odpowiada.", $"The server {url} does not respond.");
    public static string InvalidServerUrl(string raw) => T(
        $"To nie jest adres serwera: {raw}", $"This is not a server address: {raw}");
    public static string CodeFormat => T(
        "Kod parowania ma 8 cyfr (np. 1234 5678).", "The pairing code has 8 digits (e.g. 1234 5678).");

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

    // === Install, update, update-config, uninstall: steps (what the parent sees) ===

    public static string StepPairing => T("Parowanie z serwerem", "Pairing with the server");
    public static string StepStopping => T("Zatrzymywanie usługi Vigil", "Stopping the Vigil service");
    public static string StepFiles => T("Kopiowanie plików", "Copying files");
    public static string StepConfig => T("Zapisywanie konfiguracji", "Saving the configuration");
    public static string StepService => T("Rejestrowanie usługi", "Registering the service");
    public static string StepProtection => T("Ochrona przed wyłączeniem", "Protection against switching off");
    public static string StepPolicies => T("Ustawienia konta dziecka", "Child's account settings");
    public static string StepAcls => T("Uprawnienia do plików", "File permissions");
    public static string StepStart => T("Uruchamianie Vigila", "Starting Vigil");
    public static string StepProcesses => T("Zamykanie okien Vigila", "Closing Vigil's windows");
    public static string StepTasks => T("Usuwanie zadań i wpisów rozruchu", "Removing tasks and boot entries");
    public static string StepFilesRemove => T("Usuwanie plików", "Removing files");

    // === ... and their details ===

    public static string Paired(string url) => T($"Sparowano z {url}, klucz otrzymany", $"Paired with {url}, key received");
    public static string ServerUnreachable(string url, string error) => T(
        $"Brak połączenia z serwerem {url} ({error}).", $"Cannot reach the server {url} ({error}).");
    public static string PairingNoCode => T(
        "Ten kod nie jest już aktywny (wygasł po 10 minutach albo został użyty). Wygeneruj nowy w panelu rodzica.",
        "This code is no longer active (it expired after 10 minutes or was used). Create a new one in the parent panel.");
    public static string PairingWrongCode(int? left) => T(
        "Nieprawidłowy kod." + (left is { } n ? n > 0 ? $" Pozostałe próby: {n}." : " Kod przestał działać — wygeneruj nowy." : ""),
        "Wrong code." + (left is { } n2 ? n2 > 0 ? $" Attempts left: {n2}." : " The code no longer works — create a new one." : ""));
    public static string PairingFailed(int status) => T(
        $"Parowanie nie powiodło się (HTTP {status}).", $"Pairing failed (HTTP {status}).");

    public static string NotInstalled => T("Vigil nie jest zainstalowany na tym komputerze.", "Vigil is not installed on this computer.");
    public static string NothingToUpdate => T(
        "Brak zainstalowanej konfiguracji (vigil.toml, signing.dat) — potrzebna jest pełna instalacja z kodem parowania.",
        "No installed configuration (vigil.toml, signing.dat) — a full install with a pairing code is needed.");
    public static string SourceFilesMissing(string dir, IEnumerable<string> missing) => T(
        $"W {dir} brakuje plików paczki: {List(missing)}", $"Package files missing in {dir}: {List(missing)}");
    public static string RunFromPackage => T(
        "Uruchom instalator z rozpakowanej paczki, nie z C:\\ProgramData\\Vigil.",
        "Run the installer from the unpacked package, not from C:\\ProgramData\\Vigil.");
    public static string PrivilegeMissing(string name) => T(
        $"Brak uprawnienia {name} — uruchom jako administrator.", $"Privilege {name} missing — run as administrator.");

    public static string ServiceAbsent => T("Usługa nie istnieje", "The service does not exist");
    public static string ServiceAlreadyStopped => T("Usługa już zatrzymana", "The service is already stopped");
    public static string ServiceStoppedAsSystem => T("Zatrzymana (zwykły stop jako SYSTEM, z pożegnaniem)", "Stopped (normal stop as SYSTEM, with goodbye)");
    public static string ServiceKilled => T(
        "Zatrzymana przez zamknięcie procesu (stop jako SYSTEM nie zadziałał) — serwer zobaczy restart",
        "Stopped by ending the process (stop as SYSTEM did not work) — the server will see a restart");
    public static string ServiceStopFailed => T(
        "Nie udało się zatrzymać usługi Vigil. Uruchom komputer ponownie i spróbuj jeszcze raz.",
        "Could not stop the Vigil service. Restart the computer and try again.");
    public static string ServiceDeleteFailed => T(
        "Nie udało się usunąć poprzedniej usługi — uruchom komputer ponownie i spróbuj jeszcze raz.",
        "Could not remove the previous service — restart the computer and try again.");
    public static string ServiceDeleteFailedReboot => T(
        "Usługa dalej istnieje — zniknie po ponownym uruchomieniu komputera.",
        "The service still exists — it will be gone after a restart.");
    public static string OldServiceRemoved => T("Poprzednia usługa usunięta", "Previous service removed");
    public static string ServiceRemoved => T("Usługa usunięta", "Service removed");
    public static string ServiceCreated => T(
        "Usługa zarejestrowana (start automatyczny, restart×3 po awarii, bez restartu komputera)",
        "Service registered (automatic start, restart×3 on failure, no computer restart)");
    public static string ServiceDaclSet => T("DACL usługi: Admin tylko query/start", "Service DACL: Admin query/start only");
    public static string ServiceDaclFailed(int rc, string output) => T(
        $"sc sdset nie powiodło się ({rc}): {output} — Admin może zatrzymać usługę", $"sc sdset failed ({rc}): {output} — Admin can stop the service");
    public static string ServiceStarted => T("Usługa uruchomiona", "Service started");
    public static string ServiceStartedAsSystem => T("Usługa uruchomiona (jako SYSTEM)", "Service started (as SYSTEM)");
    public static string ServiceRunning => T("Usługa działa", "The service is running");
    public static string ServiceDidNotStart => T(
        "Usługa Vigil nie uruchomiła się. Szczegóły w Podglądzie zdarzeń (Aplikacja, źródło Vigil Watchdog).",
        "The Vigil service did not start. Details in Event Viewer (Application, source Vigil Watchdog).");
    public static string ServiceStoppedAfterStart => T(
        "Usługa Vigil zatrzymała się zaraz po starcie. Szczegóły w Podglądzie zdarzeń (Aplikacja, źródło Vigil Watchdog).",
        "The Vigil service stopped right after starting. Details in Event Viewer (Application, source Vigil Watchdog).");
    public static string ScFailed(string command, int rc, string output) => T(
        $"sc {command} nie powiodło się ({rc}): {output}", $"sc {command} failed ({rc}): {output}");
    public static string ToolFailed(string tool, int rc, string output) => T(
        $"{tool} zwrócił {rc}: {output}", $"{tool} returned {rc}: {output}");

    public static string ProcessStopped(string name, int pid) => T($"Zamknięto {name} (pid {pid})", $"Closed {name} (pid {pid})");
    public static string ProcessStopFailed(string name, string error) => T($"Nie udało się zamknąć {name}: {error}", $"Could not close {name}: {error}");
    public static string LegacyRemoved(string path) => T($"Usunięto pozostałość starszej wersji: {path}", $"Removed a leftover of an older version: {path}");
    public static string LegacyRemoveFailed(string path, string error) => T($"Nie udało się usunąć {path}: {error}", $"Could not remove {path}: {error}");

    public static string GuardianHashWritten(string prefix) => T($"guardian.dat: SHA-256 {prefix}… (DPAPI)", $"guardian.dat: SHA-256 {prefix}… (DPAPI)");
    public static string ConfigWritten(string url) => T($"vigil.toml: server_url = {url}", $"vigil.toml: server_url = {url}");
    public static string SigningKeyWritten => T("signing.dat: klucz zaszyfrowany (DPAPI)", "signing.dat: key encrypted (DPAPI)");
    public static string KeyKept => T("signing.dat i totp.dat bez zmian", "signing.dat and totp.dat unchanged");

    public static string SafeBootAdded => T("SafeBoot Minimal + Network", "SafeBoot Minimal + Network");
    public static string SafeBootRemoved => T("Wpisy SafeBoot usunięte", "SafeBoot entries removed");
    public static string GuardianTaskCreated(string name) => T($"Zadanie '{name}' (przy starcie + co 5 min)", $"Task '{name}' (at startup + every 5 min)");
    public static string TaskRemoved(string name) => T($"Zadanie '{name}' usunięte", $"Task '{name}' removed");
    public static string TaskRemoveFailed(string name) => T($"Nie udało się usunąć zadania '{name}'", $"Could not remove task '{name}'");
    public static string TaskCreateFailed(string name, string output) => T($"Nie udało się utworzyć zadania '{name}': {output}", $"Could not create task '{name}': {output}");
    public static string TaskRunFailed(string name, string output) => T($"Nie udało się uruchomić zadania '{name}': {output}", $"Could not run task '{name}': {output}");
    public static string BootMenuDisabled => T("Menu F8 wyłączone (bootmenupolicy Standard)", "F8 menu disabled (bootmenupolicy Standard)");
    public static string WinReDisabled => T("WinRE wyłączone", "WinRE disabled");
    public static string BootMenuNotRestored => T(
        "Menu F8 i WinRE nie są przywracane: Standard to domyślne ustawienie Windows 10/11; wyłączone WinRE włącz poleceniem 'reagentc /enable'.",
        "The F8 menu and WinRE are not restored: Standard is the Windows 10/11 default; enable a disabled WinRE with 'reagentc /enable'.");

    public static string NoChildDefaultOnly => T(
        "Brak konta dziecka — ustawienia tylko w Default profile (konta tworzone później)",
        "No child's account — settings in the Default profile only (accounts created later)");
    public static string PoliciesApplied(string label, bool live) => T(
        $"{label}: ustawione{(live ? " (zalogowane)" : "")}", $"{label}: set{(live ? " (signed in)" : "")}");
    public static string PoliciesSkipped => T("Pominięte (-NoUserPolicies)", "Skipped (-NoUserPolicies)");
    public static string AclsSet => T(
        "Katalog: SYSTEM full, Admin modify, AuthUsers r+x; sekrety: SYSTEM full, Admin read",
        "Folder: SYSTEM full, Admin modify, AuthUsers r+x; secrets: SYSTEM full, Admin read");
    public static string FolderRemoved(string dir) => T($"{dir} usunięty", $"{dir} removed");
}
