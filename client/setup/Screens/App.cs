using Spectre.Console;
using Vigil.Setup.Core;
using Vigil.Shared;

namespace Vigil.Setup.Screens;

/// <summary>
/// vigil_setup.exe without parameters: the wizard when nothing is installed, then (or straight away) the
/// installation's status and a menu. Screens only: every change goes through the core (Installer, Verify),
/// the same calls the parameter mode makes.
/// </summary>
internal sealed class App
{
    public IAnsiConsole C { get; }
    public LogReport Log { get; }
    /// <summary>-ChildUsername given with the wizard: no question about the account.</summary>
    public string? ChildUserName { get; }
    /// <summary>The package this exe carries (#76); null in the installed copy or a damaged download.</summary>
    public Package? Package { get; }
    private readonly string? _packageError;

    private App(IAnsiConsole console, LogReport log, string? childUserName)
    {
        C = console;
        Log = log;
        ChildUserName = childUserName;
        try { Package = Package.OfThisProgram(); }
        catch (SetupException ex) { _packageError = ex.Message; }
    }

    /// <summary>Why there is nothing to install from; null when the package is fine.</summary>
    public string? PackageProblem => Package is not null ? null
        : _packageError ?? Strings.NoPackage(Environment.ProcessPath ?? VigilLayout.SetupExe);

    public static int Run(Cli cli, LogReport log)
    {
        var console = cli.KeysFile is { } keys ? ScriptedConsole.Create(keys) : AnsiConsole.Console;
        var app = new App(console, log, cli.ChildUserName);
        try
        {
            if (!Installer.IsInstalled())
            {
                Wizard.Run(app);
                if (!Installer.IsInstalled()) return 0;
            }
            return MainMenu.Run(app);
        }
        catch (ScriptEndedException ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 3;
        }
        catch (Exception ex)
        {
            log.Error(ex);
            console.WriteLine();
            Theme.Problem(console, CheckLevel.Fail, ex.Message);
            app.ShowLogPath();
            return 1;
        }
    }

    public void ShowLogPath()
    {
        if (Log.Path is not null) Theme.Hint(C, Strings.UiDetailsInLog(Log.Path));
    }

    // === Shared steps of the wizard and the menu ===

    /// <summary>
    /// Asks for the server address until /health answers. Returns the normalized address; with
    /// <paramref name="emptyGoesBack"/> an empty field confirmed returns null (back to the menu).
    /// </summary>
    public string? AskServer(Action header, string? initial, string help, bool emptyGoesBack = false)
    {
        string? error = null;
        var typed = initial ?? "";
        while (true)
        {
            header();
            Theme.Text(C, help);
            Theme.Hint(C, Strings.UiServerExample);
            Theme.Blank(C);
            if (error is not null)
            {
                Theme.Problem(C, CheckLevel.Fail, error);
                Theme.Blank(C);
            }
            typed = Prompts.Line(C, Strings.UiServerPrompt, typed);
            if (typed.Length == 0)
            {
                if (emptyGoesBack) return null;
                error = Strings.UiServerInvalid;
                continue;
            }
            var url = Pairing.NormalizeUrl(typed);
            if (!Pairing.IsValidUrl(url))
            {
                error = Strings.UiServerInvalid;
                continue;
            }
            var ok = C.Status().Spinner(Theme.SpinnerFor(C)).SpinnerStyle(Theme.Accent)
                .Start(Theme.Esc(Strings.UiServerChecking(url)), _ => Pairing.IsHealthy(url));
            Log.Info($"wizard: server {url} {(ok ? "responds" : "does not respond")}");
            if (ok) return url;
            error = Strings.UiServerNoAnswer(url);
        }
    }

    /// <summary>Asks for an 8-digit code (format only: it is used later, after the summary).</summary>
    public string AskCode(Action header, string help, string? error)
    {
        while (true)
        {
            header();
            Theme.Text(C, help);
            Theme.Hint(C, Strings.UiCodeValidity);
            Theme.Blank(C);
            if (error is not null)
            {
                Theme.Problem(C, CheckLevel.Fail, error);
                Theme.Blank(C);
            }
            var code = Pairing.NormalizeCode(Prompts.Line(C, Strings.UiCodePrompt));
            if (code is not null) return code;
            error = Strings.CodeFormat;
        }
    }

    /// <summary>The child's account: given, the only one, or chosen from a list; null = Default profile only.</summary>
    public UserAccount? AskAccount(Action header)
    {
        var resolved = ChildAccountChoice.Resolve(ChildUserName);
        if (resolved.Account is not null || ChildUserName is not null) return resolved.Account;
        var accounts = Accounts.Interactive();
        if (accounts.Count < 2) return null;

        header();
        Theme.Text(C, Strings.UiAccountHelp);
        Theme.Blank(C);
        var items = accounts.Select((a, i) => new Choice<UserAccount>((char)('1' + i % 9), a.Name, a)).ToList();
        return Prompts.Menu(C, items);
    }

    /// <summary>
    /// Pairs and then applies <paramref name="apply"/> with the key, behind the steps view. A wrong or expired
    /// code (nothing changed yet) asks for the code again; the server not answering offers a retry.
    /// An error after pairing is shown in the steps (Failed: something may have changed).
    /// </summary>
    public PairResult PairAndApply(Action header, string serverUrl, string code, string codeHelp,
        Action<Installer, string> apply)
    {
        while (true)
        {
            header();
            var view = new StepsView(C);
            PairingException? pairingError = null;
            var error = StepsView.Run(C, Log, view, report =>
            {
                var installer = new Installer(report, Package);
                string key;
                try { key = installer.Pair(serverUrl, code); }
                catch (PairingException ex) { pairingError = ex; throw; }
                apply(installer, key);
            });
            if (error is null) return new(PairOutcome.Done, view.Warnings);
            if (pairingError is null) return new(PairOutcome.Failed, view.Warnings);
            if (pairingError.Failure is PairingFailure.NoCode or PairingFailure.WrongCode)
            {
                code = AskCode(header, codeHelp, pairingError.Message);
                continue;
            }
            // The server did not answer: nothing changed.
            C.WriteLine();
            Theme.Text(C, Strings.UiServerTrouble);
            var next = Prompts.Menu(C, new[]
            {
                new Choice<PairOutcome?>('1', Strings.UiChoiceRetry, null),
                new Choice<PairOutcome?>('2', Strings.UiChoiceOtherServer, PairOutcome.OtherServer),
                new Choice<PairOutcome?>('0', Strings.UiChoiceCancel, PairOutcome.Cancelled),
            }, escapeKey: '0');
            if (next is { } giveUp) return new(giveUp, 0);
        }
    }

    /// <summary>Verify after a change, as a short table.</summary>
    public VerifyView VerifyShort(UserAccount? child)
    {
        C.WriteLine();
        var view = VerifyView.Run(C, Log, child?.Name ?? ChildUserName, Package);
        view.RenderShort(C);
        return view;
    }

    public static InstallRequest Request(string serverUrl, string? key, UserAccount? child) => new()
    {
        ServerUrl = serverUrl,
        SigningKey = key,
        Child = child,
    };

    public static string? InstalledVersion()
    {
        var v = AppVersion.Of(VigilLayout.PathOf(VigilLayout.WatchdogExe));
        return AppVersion.IsUnversioned(v) ? null : v;
    }
}

internal enum PairOutcome { Done, Failed, Cancelled, OtherServer }

internal sealed record PairResult(PairOutcome Outcome, int Warnings);
