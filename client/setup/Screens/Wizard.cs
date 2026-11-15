using Spectre.Console;
using Vigil.Setup.Core;

namespace Vigil.Setup.Screens;

/// <summary>
/// Nothing installed: server address → pairing code → the child's account → summary → install → verify.
/// Nothing on the PC changes before "Install" on the summary; the code is used only then, and a wrong or expired
/// one asks again with the system untouched.
///
/// The child's account (D30, #160): one account that is an administrator — the typical PC — gets a choice,
/// by default a new standard account for the child (then, when the current account has no password, a password
/// for it: it becomes the parent's); several accounts — the one the child signs in to (a standard one: the
/// parent's password too, when the current account has none).
/// </summary>
internal static class Wizard
{
    private enum Setup { NewChild, OneAccount }

    public static void Run(App app)
    {
        var c = app.C;
        if (app.PackageProblem is { } problem)
        {
            Theme.Header(c, Strings.UiSetupTitle);
            Theme.Problem(c, CheckLevel.Fail, problem);
            Prompts.Continue(c, Strings.UiPressEnterExit);
            return;
        }

        var accounts = Accounts.Interactive();
        var offerNewChild = app.ChildUserName is null && accounts.Count == 1 && LocalAccounts.IsAdministrator(accounts[0].Sid);
        var severalAccounts = app.ChildUserName is null && accounts.Count > 1;
        var parent = LocalAccounts.Current();
        // Asked at most once: every try counts towards the account lockout threshold.
        bool? parentBlank = null;
        bool ParentBlank() => parentBlank ??= LocalAccounts.HasBlankPassword(parent.Name);
        var parentNeedsPassword = offerNewChild && ParentBlank();
        var autoLogon = LocalAccounts.AutoLogonUser();

        // The step count follows the choices (the default first: a new account).
        var total = offerNewChild ? (parentNeedsPassword ? 6 : 5) : severalAccounts ? 4 : 3;
        Action Header(int n, string title) => () => Theme.Header(c, Strings.UiSetupTitle, Strings.UiStep(n, total, title));

        string? url = null;
        while (true)
        {
            url = app.AskServer(Header(1, Strings.UiServerTitle), url, Strings.UiServerHelp)!;
            var code = app.AskCode(Header(2, Strings.UiCodeTitle), Strings.UiCodeHelp, null);

            UserAccount? child = null;
            NewChildAccount? newChild = null;
            string? parentPassword = null;
            if (offerNewChild)
            {
                while (true)
                {
                    var setup = AskSetup(app, () =>
                    {
                        total = parentNeedsPassword ? 6 : 5;  // also after "Back" from the limits
                        Header(3, Strings.UiAccountTitle)();
                    }, () =>
                    {
                        total = 4;
                        Header(3, Strings.UiOneAccountTitle)();
                    }, accounts[0], parent.Name);
                    if (setup is null)
                    {
                        Cancelled(app);
                        return;
                    }
                    if (setup == Setup.OneAccount)
                    {
                        total = 4;
                        child = accounts[0];
                        break;
                    }
                    newChild = AskNewChild(app, Header(4, Strings.UiNewChildTitle));
                    if (newChild is null) continue;  // an empty name: back to the choice
                    if (parentNeedsPassword)
                        parentPassword = AskParentPassword(app, Header(5, Strings.UiParentPasswordTitle), parent.Name, autoLogon);
                    break;
                }
            }
            else
            {
                total = severalAccounts ? 4 : 3;
                child = severalAccounts ? app.AskAccount(Header(3, Strings.UiAccountTitle))
                    : ChildAccountChoice.Resolve(app.ChildUserName).Account;
                // A standard account that exists: the parent's account needs a password as well (D30).
                if (child is not null && !LocalAccounts.IsAdministrator(child.Sid) && ParentBlank())
                {
                    total++;
                    parentPassword = AskParentPassword(app, Header(total - 1, Strings.UiParentPasswordTitle), parent.Name,
                        autoLogon is not null && !autoLogon.Equals(child.Name, StringComparison.OrdinalIgnoreCase) ? autoLogon : null);
                }
            }

            Header(total, Strings.UiSummaryTitle)();
            if (newChild is not null) SummaryNewChild(c, url, newChild.Name, parent.Name, parentPassword is not null, autoLogon);
            else Summary(c, url, child, parent.Name, parentPassword is not null, autoLogon);
            var choice = Prompts.Menu(c, new[]
            {
                new Choice<int>('1', Strings.UiChoiceInstall, 1),
                new Choice<int>('2', Strings.UiChoiceEdit, 2),
                new Choice<int>('0', Strings.UiChoiceCancel, 0),
            }, escapeKey: '0');
            if (choice == 0)
            {
                Cancelled(app);
                return;
            }
            if (choice == 2) continue;

            app.Log.Info($"wizard: install, server {url}, account " +
                         (newChild is not null ? $"{newChild.Name} (new){(parentPassword is not null ? ", parent's password set" : "")}"
                             : child?.Name ?? "(Default profile only)"));
            var result = app.PairAndApply(() => Theme.Header(c, Strings.UiSetupTitle, Strings.UiInstalling), url, code,
                Strings.UiCodeHelp, (installer, key) => installer.Install(App.Request(url, key, child) with
                {
                    NewChild = newChild,
                    ParentPassword = parentPassword,
                }));
            switch (result.Outcome)
            {
                case PairOutcome.OtherServer:
                    continue;
                case PairOutcome.Cancelled:
                    Cancelled(app);
                    return;
                case PairOutcome.Failed:
                    // Something ran after pairing: the menu (Repair) takes it from here.
                    app.ShowLogPath();
                    Prompts.Continue(c, Strings.UiPressEnter);
                    return;
            }

            c.WriteLine();
            c.MarkupLine($"{Theme.Mark(c, CheckLevel.Pass)} [green bold]{Theme.Esc(Strings.UiInstalled)}[/]");
            app.VerifyShort(newChild is null ? child : null);
            if (newChild is not null)
            {
                Theme.Blank(c);
                Theme.Text(c, Strings.UiSwitchToChild(newChild.Name));
            }
            Prompts.Continue(c, Strings.UiPressEnter);
            return;
        }
    }

    /// <summary>One account, an administrator: a new account for the child (default) or this one, after its limits.
    /// Null: cancelled.</summary>
    private static Setup? AskSetup(App app, Action header, Action limitsHeader, UserAccount only, string parent)
    {
        var c = app.C;
        while (true)
        {
            header();
            Theme.Text(c, Strings.UiOneAdminAccount(only.Name));
            Theme.Blank(c);
            Theme.Text(c, Strings.UiAdminCanBypass);
            Theme.Blank(c);
            c.MarkupLine($"[bold]{Theme.Esc(Strings.UiRecommendNewChild)}[/]");
            Theme.Bullet(c, Strings.UiNewChildPoint1);
            Theme.Bullet(c, Strings.UiNewChildPoint2(parent));
            Theme.Bullet(c, Strings.UiNewChildPoint3);
            Theme.Blank(c);
            var setup = Prompts.Menu(c, new[]
            {
                new Choice<Setup?>('1', Strings.UiChoiceNewChild, Setup.NewChild),
                new Choice<Setup?>('2', Strings.UiChoiceOneAccount, Setup.OneAccount),
                new Choice<Setup?>('0', Strings.UiChoiceCancel, null),
            }, escapeKey: '0');
            if (setup != Setup.OneAccount) return setup;

            limitsHeader();
            Theme.Text(c, Strings.UiOneAccountIntro(only.Name));
            Theme.Blank(c);
            Theme.Problem(c, CheckLevel.Warn, Strings.UiOneAccountLimit1);
            Theme.Problem(c, CheckLevel.Warn, Strings.UiOneAccountLimit2);
            Theme.Problem(c, CheckLevel.Warn, Strings.UiOneAccountLimit3);
            Theme.Blank(c);
            Theme.Hint(c, Strings.UiOneAccountLater);
            Theme.Blank(c);
            var confirm = Prompts.Menu(c, new[]
            {
                new Choice<int>('1', Strings.UiChoiceUnderstand, 1),
                new Choice<int>('2', Strings.UiChoiceBack, 2),
                new Choice<int>('0', Strings.UiChoiceCancel, 0),
            }, escapeKey: '0');
            if (confirm == 1) return Setup.OneAccount;
            if (confirm == 0) return null;
        }
    }

    /// <summary>The new account's name and optional password; null for an empty name (back).</summary>
    private static NewChildAccount? AskNewChild(App app, Action header)
    {
        var c = app.C;
        string? error = null;
        var name = "";
        while (true)
        {
            header();
            Theme.Text(c, Strings.UiNewChildHelp);
            Theme.Hint(c, Strings.UiNewChildHint);
            Theme.Hint(c, Strings.UiEmptyNameBack);
            Theme.Blank(c);
            if (error is not null)
            {
                Theme.Problem(c, CheckLevel.Fail, error);
                Theme.Blank(c);
            }
            name = Prompts.Line(c, Strings.UiNewChildName, name);
            if (name.Length == 0) return null;
            if (LocalAccounts.NameProblem(name) is { } problem)
            {
                error = problem;
                continue;
            }
            var password = Prompts.Secret(c, Strings.UiNewChildPassword);
            if (password.Length > 0 && Prompts.Secret(c, Strings.UiRepeatPassword) != password)
            {
                error = Strings.UiPasswordsDiffer;
                continue;
            }
            return new NewChildAccount(name, password.Length > 0 ? password : null);
        }
    }

    /// <summary>A first password for the parent's account, required.</summary>
    private static string AskParentPassword(App app, Action header, string parent, string? autoLogon)
    {
        var c = app.C;
        string? error = null;
        while (true)
        {
            header();
            Theme.Text(c, Strings.UiParentPasswordHelp(parent));
            Theme.Hint(c, Strings.UiParentPasswordKeep);
            Theme.Hint(c, Strings.UiParentPasswordLater);
            if (autoLogon is not null) Theme.Problem(c, CheckLevel.Warn, Strings.UiAutoLogonWillBeOff(autoLogon));
            Theme.Blank(c);
            if (error is not null)
            {
                Theme.Problem(c, CheckLevel.Fail, error);
                Theme.Blank(c);
            }
            var password = Prompts.Secret(c, Strings.UiNewPassword);
            if (password.Length == 0)
            {
                error = Strings.UiParentPasswordRequired;
                continue;
            }
            if (Prompts.Secret(c, Strings.UiRepeatPassword) != password)
            {
                error = Strings.UiPasswordsDiffer;
                continue;
            }
            return password;
        }
    }

    private static void Summary(IAnsiConsole c, string url, UserAccount? child, string parent, bool newPassword,
        string? autoLogon)
    {
        // A standard account that exists: the standard-account setup without creating one.
        var standard = child is not null && !LocalAccounts.IsAdministrator(child.Sid);
        c.Write(Theme.Facts(new[]
        {
            (Strings.UiSummaryServer, $"[{Theme.AccentTag}]{Theme.Esc(url)}[/]"),
            (Strings.UiSummaryAccount, child is null ? $"[yellow]{Theme.Esc(Strings.UiSummaryNoAccount)}[/]"
                : Theme.Esc(child.Name) + (standard ? $" [grey]— {Theme.Esc(Strings.UiSummaryStandard)}[/]" : "")),
            (Strings.UiSummaryVersion, Theme.Esc(AppVersion.Display)),
        }));
        Theme.Blank(c);
        c.MarkupLine($"[bold]{Theme.Esc(Strings.UiSummaryWhat)}[/]");
        if (standard)
        {
            // Automatic sign-in to the child's own account stays; to any other it is turned off.
            var autoOff = autoLogon is not null && !autoLogon.Equals(child!.Name, StringComparison.OrdinalIgnoreCase);
            if (newPassword) Theme.Bullet(c, Strings.UiSummaryParentPassword(parent, autoOff));
            else if (autoOff) Theme.Bullet(c, Strings.UiSummaryAutoLogonOff);
            Theme.Bullet(c, Strings.UiSummaryServiceChild(child!.Name));
            Theme.Bullet(c, Strings.UiSummaryLockChild(child.Name));
            Theme.Bullet(c, Strings.UiSummaryPoliciesChild(child.Name));
        }
        else
        {
            Theme.Bullet(c, Strings.UiSummaryService);
            Theme.Bullet(c, Strings.UiSummaryLock);
            Theme.Bullet(c, Strings.UiSummaryPolicies(child?.Name));
        }
        if (child is null) Theme.Problem(c, CheckLevel.Warn, Strings.UiAccountNone);
        Theme.Blank(c);
        Theme.Hint(c, Strings.UiSummaryNothingYet);
        Theme.Blank(c);
    }

    private static void SummaryNewChild(IAnsiConsole c, string url, string child, string parent, bool newPassword,
        string? autoLogon)
    {
        c.Write(Theme.Facts(new[]
        {
            (Strings.UiSummaryServer, $"[{Theme.AccentTag}]{Theme.Esc(url)}[/]"),
            (Strings.UiSummaryAccount, $"{Theme.Esc(child)} [grey]— {Theme.Esc(Strings.UiSummaryNewStandard)}[/]"),
            (Strings.UiSummaryYourAccount, $"{Theme.Esc(parent)} [grey]— {Theme.Esc(Strings.UiSummaryAdmin(newPassword))}[/]"),
            (Strings.UiSummaryVersion, Theme.Esc(AppVersion.Display)),
        }));
        Theme.Blank(c);
        c.MarkupLine($"[bold]{Theme.Esc(Strings.UiSummaryWhat)}[/]");
        Theme.Bullet(c, Strings.UiSummaryCreate(child));
        if (newPassword) Theme.Bullet(c, Strings.UiSummaryParentPassword(parent, autoLogon is not null));
        else if (autoLogon is not null) Theme.Bullet(c, Strings.UiSummaryAutoLogonOff);
        Theme.Bullet(c, Strings.UiSummaryServiceChild(child));
        Theme.Bullet(c, Strings.UiSummaryLockChild(child));
        Theme.Bullet(c, Strings.UiSummaryPoliciesChild(child));
        Theme.Blank(c);
        Theme.Hint(c, Strings.UiSummaryNothingYet);
        Theme.Blank(c);
    }

    private static void Cancelled(App app)
    {
        app.Log.Info("wizard: cancelled");
        app.C.WriteLine();
        Theme.Text(app.C, Strings.UiCancelled);
        Prompts.Continue(app.C, Strings.UiPressEnterExit);
    }
}
