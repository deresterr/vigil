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

    // === Wizard and menu (Screens/) ===

    public static string UiSetupTitle => T("Instalacja", "Setup");
    public static string UiMenuTitle => T("Instalator", "Installer");
    public static string UiStep(int n, int total, string title) => T($"Krok {n} z {total} · {title}", $"Step {n} of {total} · {title}");
    public static string UiMenuHint => T("Strzałki i Enter albo cyfra", "Arrow keys and Enter, or a digit");
    public static string UiFieldHint => T("Enter zatwierdza, Esc czyści pole", "Enter confirms, Esc clears the field");
    public static string UiPressEnter => T("Enter — dalej", "Enter — continue");
    public static string UiPressEnterMenu => T("Enter — powrót do menu", "Enter — back to the menu");
    public static string UiPressEnterExit => T("Enter — zakończ", "Enter — exit");
    public static string UiDetailsInLog(string path) => T($"Szczegóły w logu: {path}", $"Details in the log: {path}");
    public static string UiWorking => T("Chwileczkę…", "One moment…");
    public static string UiCancelled => T("Instalacja przerwana — niczego nie zmieniono.", "Setup cancelled — nothing was changed.");
    public static string UiScriptEnded => T("Skrypt klawiszy (-Keys) się skończył.", "The key script (-Keys) ran out.");
    public static string NoPackage(string path) => T(
        $"Ten vigil_setup.exe nie zawiera paczki ({path}): to kopia z C:\\ProgramData\\Vigil albo niepełne pobranie. Uruchom pobrany vigil_setup.exe.",
        $"This vigil_setup.exe carries no package ({path}): a copy from C:\\ProgramData\\Vigil or an incomplete download. Run the downloaded vigil_setup.exe.");

    // Server address
    public static string UiServerTitle => T("Adres serwera", "Server address");
    public static string UiServerHelp => T(
        "Adres serwera Vigil w Twojej sieci — ten sam, pod którym otwierasz panel rodzica.",
        "The address of the Vigil server on your network — the one you open the parent panel at.");
    public static string UiServerExample => T(
        "Na przykład 192.168.1.5 albo http://192.168.1.5:8088 (bez portu przyjmę 8088).",
        "For example 192.168.1.5 or http://192.168.1.5:8088 (without a port, 8088).");
    public static string UiServerChangeHelp => T(
        "Ten sam serwer pod innym adresem (np. nowe IP): klucz komputera zostaje. Inny serwer wymaga ponownego parowania.",
        "The same server at a different address (e.g. a new IP): the computer's key stays. Another server needs re-pairing.");
    public static string UiEmptyGoesBack => T("Puste pole i Enter — powrót do menu.", "An empty field and Enter — back to the menu.");
    public static string UiServerPrompt => T("Adres serwera:", "Server address:");
    public static string UiServerChecking(string url) => T($"Sprawdzam połączenie z {url}…", $"Checking the connection to {url}…");
    public static string UiServerInvalid => T(
        "To nie wygląda na adres serwera. Wpisz adres IP albo nazwę, np. 192.168.1.5.",
        "That does not look like a server address. Type an IP address or a name, e.g. 192.168.1.5.");
    public static string UiServerNoAnswer(string url) => T(
        $"Serwer {url} nie odpowiada. Sprawdź adres i czy serwer działa (panel rodzica otwiera się pod nim w przeglądarce).",
        $"The server {url} does not respond. Check the address and that the server is running (the parent panel opens at it in a browser).");

    // Pairing code
    public static string UiCodeTitle => T("Kod parowania", "Pairing code");
    public static string UiCodeHelp => T(
        "Kod łączy ten komputer z dzieckiem w panelu. Wygeneruj go w panelu rodzica: Ustawienia → Komputery dziecka → Dodaj komputer.",
        "The code links this computer to the child in the panel. Create it in the parent panel: Settings → Child's computers → Add computer.");
    public static string UiCodeHelpRepair => T(
        "Nowy klucz dla tego komputera. Wygeneruj kod w panelu rodzica: Ustawienia → Komputery dziecka → Sparuj ponownie (przy tym komputerze).",
        "A new key for this computer. Create the code in the parent panel: Settings → Child's computers → Re-pair (next to this computer).");
    public static string UiCodeValidity => T("Kod ma 8 cyfr i działa 10 minut.", "The code has 8 digits and works for 10 minutes.");
    public static string UiCodePrompt => T("Kod parowania:", "Pairing code:");
    public static string UiCodeNeeded => T(
        "Brakuje klucza tego komputera (signing.dat) — potrzebny jest kod parowania.",
        "This computer's key (signing.dat) is missing — a pairing code is needed.");

    // Child's account
    public static string UiAccountTitle => T("Konto dziecka", "Child's account");
    public static string UiAccountHelp => T(
        "Na tym komputerze jest kilka kont. Wybierz to, na którym loguje się dziecko — na nie trafią ustawienia ochrony.",
        "There are several accounts on this computer. Choose the one the child signs in to — the protection settings go to it.");
    public static string UiAccountNone => T(
        "Nie znaleziono konta dziecka — ustawienia trafią tylko do profilu domyślnego (konta tworzone później).",
        "No child's account found — the settings go to the default profile only (accounts created later).");

    // Summary
    public static string UiSummaryTitle => T("Podsumowanie", "Summary");
    public static string UiSummaryServer => T("Serwer", "Server");
    public static string UiSummaryAccount => T("Konto dziecka", "Child's account");
    public static string UiSummaryVersion => T("Wersja", "Version");
    public static string UiSummaryNoAccount => T("brak — tylko profil domyślny", "none — default profile only");
    public static string UiSummaryWhat => T("Co się stanie:", "What happens:");
    public static string UiSummaryService => T(
        "Vigil zostanie zainstalowany jako usługa systemowa i od razu zacznie liczyć czas dziecka.",
        "Vigil is installed as a system service and starts counting the child's time right away.");
    public static string UiSummaryLock => T(
        "Gdy skończy się limit albo zacznie cisza nocna, komputer pokaże ekran blokady.",
        "When the limit runs out or quiet hours start, the computer shows the lock screen.");
    public static string UiSummaryPolicies(string? account) => T(
        $"Na koncie {account ?? "domyślnym"} zostaną wyłączone Menedżer zadań, zmiana hasła, skróty ułatwień dostępu i gesty touchpada. To jedno konto, więc dotyczy to też Ciebie.",
        $"On the {account ?? "default"} account Task Manager, password change, accessibility shortcuts and touchpad gestures are turned off. It is one account, so this applies to you too.");
    public static string UiSummaryNothingYet => T(
        "Do tej chwili niczego nie zmieniono. Kod zostanie użyty dopiero po wybraniu „Zainstaluj”.",
        "Nothing has been changed so far. The code is used only once you choose “Install”.");
    public static string UiChoiceInstall => T("Zainstaluj", "Install");
    public static string UiChoiceEdit => T("Popraw dane", "Change the details");
    public static string UiChoiceCancel => T("Anuluj", "Cancel");

    // Progress and results
    public static string UiInstalling => T("Instalacja", "Installing");
    public static string UiInstalled => T("Gotowe — Vigil działa na tym komputerze.", "Done — Vigil runs on this computer.");
    public static string UiActionDone => T("Gotowe.", "Done.");
    public static string UiActionDoneWarnings => T("Gotowe, z ostrzeżeniami (wyżej).", "Done, with warnings (above).");
    public static string UiActionFailed => T("Nie udało się.", "It did not work.");
    public static string UiServerTrouble => T("Co dalej?", "What now?");
    public static string UiChoiceRetry => T("Spróbuj ponownie", "Try again");
    public static string UiChoiceOtherServer => T("Zmień adres serwera", "Change the server address");
    public static string UiVerifyTitle => T("Sprawdzenie instalacji", "Installation check");
    public static string UiVerifyRunning => T("Sprawdzam instalację…", "Checking the installation…");
    public static string UiVerifyArea => T("Obszar", "Area");
    public static string UiVerifyResult => T("Wynik", "Result");
    public static string UiVerifyAllOk => T("Wszystko w porządku.", "Everything is in order.");
    public static string UiVerifyProblems(int fail, int warn) => T(
        $"Błędy: {fail}, ostrzeżenia: {warn} — szczegóły wyżej i w logu.",
        $"Failures: {fail}, warnings: {warn} — details above and in the log.");

    // Menu: status
    public static string UiStatusChecking => T("Sprawdzam stan…", "Checking the status…");
    public static string UiStatusVersion => T("Wersja", "Version");
    public static string UiStatusVersionValue(string installed, string package) => T(
        $"{installed} (w paczce: {package})", $"{installed} (in this package: {package})");
    public static string UiVersionUnknown => T("nieznana", "unknown");
    public static string UiStatusServer => T("Serwer", "Server");
    public static string UiStatusResponds => T("odpowiada", "responds");
    public static string UiStatusNoAnswer => T("nie odpowiada", "does not respond");
    public static string UiStatusNoConfig => T("brak konfiguracji", "no configuration");
    public static string UiStatusService => T("Usługa", "Service");
    public static string UiServiceRunning => T("działa", "running");
    public static string UiServiceStopped => T("zatrzymana", "stopped");
    public static string UiServiceMissing => T("nie zarejestrowana", "not registered");
    public static string UiServiceOther(string state) => T($"stan: {state}", $"state: {state}");
    public static string UiStatusKey => T("Klucz komputera", "Computer's key");
    public static string UiKeyPresent => T("jest", "present");
    public static string UiKeyMissing => T("brak — potrzebne parowanie", "missing — pairing needed");
    public static string UiStatusAccount => T("Konto dziecka", "Child's account");
    public static string UiAccountSeveral => T("kilka kont — zapytam przy zmianach", "several accounts — asked when needed");

    // Menu: items
    public static string UiMenuVerify => T("Sprawdź instalację", "Check the installation");
    public static string UiMenuUpdate(string version) => T($"Zaktualizuj do {version}", $"Update to {version}");
    public static string UiMenuRepair => T("Napraw (zainstaluj ponownie pliki tej wersji)", "Repair (reinstall this version's files)");
    public static string UiMenuDowngrade(string version) => T(
        $"Zainstaluj starszą wersję z paczki ({version})", $"Install the older version from this package ({version})");
    public static string UiMenuServer => T("Zmień adres serwera", "Change the server address");
    public static string UiMenuRepair2 => T("Sparuj ponownie", "Re-pair");
    public static string UiMenuUninstall => T("Odinstaluj", "Uninstall");
    public static string UiMenuExit => T("Zakończ", "Exit");

    // Menu: confirmations
    public static string UiConfirmUpdate(string from, string to) => T(
        $"Zainstalować wersję {to} (teraz {from})? Usługa zatrzyma się na chwilę; adres serwera i klucz komputera zostają.",
        $"Install version {to} (now {from})? The service stops for a moment; the server address and the computer's key stay.");
    public static string UiConfirmUninstall => T(
        "Odinstalować Vigil? Usługa zostanie zatrzymana, a pliki i ustawienia konta usunięte. W Dzienniku panelu rodzica pojawi się wpis o zatrzymaniu usługi.",
        "Uninstall Vigil? The service stops, its files and account settings are removed. The parent panel's Journal shows that the service was stopped.");
    public static string UiUninstalled => T("Vigil odinstalowany.", "Vigil uninstalled.");
    public static string UiUninstallLeft => T(
        "Nie wszystko udało się usunąć — szczegóły wyżej. Uruchom komputer ponownie i odinstaluj jeszcze raz.",
        "Not everything could be removed — details above. Restart the computer and uninstall again.");

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
        $"Ścieżka: {path}" + (unquoted ? " (bez cudzysłowów)" : ""),
        $"Path: {path}" + (unquoted ? " (unquoted)" : ""));
    public static string ServiceRecovery(bool nonCrash) => T(
        "Recovery: restart×3" + (nonCrash ? "" : ", bez FailureActionsOnNonCrashFailures"),
        "Recovery: restart×3" + (nonCrash ? "" : ", without FailureActionsOnNonCrashFailures"));
    public static string ServiceDacl(string? ba) => T(
        "DACL: Admin bez stop i delete" + (ba is null ? " — brak wpisu BA" : ba.Contains("WP") || ba.Contains("SD") ? $" — BA={ba}" : ""),
        "DACL: Admin without stop and delete" + (ba is null ? " — no BA entry" : ba.Contains("WP") || ba.Contains("SD") ? $" — BA={ba}" : ""));

    public static string FilesComplete(IReadOnlyCollection<string?> missing) => T(
        "Komplet plików" + (missing.Count > 0 ? $" — brak: {List(missing)}" : ""),
        "All files present" + (missing.Count > 0 ? $" — missing: {List(missing)}" : ""));
    public static string BackupMatches => T("backup\\vigil_watchdog.exe = vigil_watchdog.exe", "backup\\vigil_watchdog.exe = vigil_watchdog.exe");
    public static string GuardianHashMatches => T("guardian.dat: hash zgodny z vigil_watchdog.exe", "guardian.dat: hash matches vigil_watchdog.exe");
    public static string DoesNotDecrypt(string file, string error) => T($"{file} nie odszyfrowuje się: {error}", $"{file} does not decrypt: {error}");
    public static string PackageMatches(string path) => T($"Zainstalowane binarki = paczka {path}", $"Installed binaries = package {path}");
    public static string SigningKeyDecrypted(int bytes) => T($"signing.dat: klucz odszyfrowany ({bytes} B)", $"signing.dat: key decrypted ({bytes} B)");
    public static string ConfigServerUrl(string? url) => T($"vigil.toml: server_url = {url}", $"vigil.toml: server_url = {url}");
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
    public static string StepEventSources => T("Usuwanie źródeł Dziennika zdarzeń", "Removing Event Log sources");
    public static string EventSourceRemoved(string source) => T($"Źródło '{source}' usunięte", $"Source '{source}' removed");
    public static string NoEventSources(IReadOnlyCollection<string> left) => left.Count == 0
        ? T("Brak źródeł Dziennika zdarzeń Vigila", "No Vigil Event Log sources")
        : T($"Zostały źródła Dziennika zdarzeń: {string.Join(", ", left)} (nieszkodliwe; odinstalowanie je usuwa)",
            $"Event Log sources left: {string.Join(", ", left)} (harmless; uninstall removes them)");
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
        "Uruchom pobrany vigil_setup.exe, nie kopię z C:\\ProgramData\\Vigil.",
        "Run the downloaded vigil_setup.exe, not the copy in C:\\ProgramData\\Vigil.");
    public static string PackageCorrupt(string path, string what) => T(
        $"Paczka {path} jest uszkodzona ({what}) — pobierz vigil_setup.exe ponownie.",
        $"The package {path} is damaged ({what}) — download vigil_setup.exe again.");
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
