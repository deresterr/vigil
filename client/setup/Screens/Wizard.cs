using Spectre.Console;
using Vigil.Setup.Core;

namespace Vigil.Setup.Screens;

/// <summary>
/// Nothing installed: server address → pairing code → (child's account, only with several) → summary →
/// install → verify. Nothing on the PC changes before "Install" on the summary; the code is used only then,
/// and a wrong or expired one asks again with the system untouched.
/// </summary>
internal static class Wizard
{
    public static void Run(App app)
    {
        var c = app.C;
        var missing = app.MissingPackageFiles();
        if (missing.Count > 0)
        {
            Theme.Header(c, Strings.UiSetupTitle);
            Theme.Problem(c, CheckLevel.Fail, Strings.UiPackageIncomplete(app.PackageDir, missing));
            Prompts.Continue(c, Strings.UiPressEnterExit);
            return;
        }

        // The account step only when there is a choice to make; the step count follows.
        var severalAccounts = app.ChildUserName is null && Accounts.Interactive().Count > 1;
        var total = severalAccounts ? 4 : 3;
        Action Header(int n, string title) => () => Theme.Header(c, Strings.UiSetupTitle, Strings.UiStep(n, total, title));

        string? url = null;
        while (true)
        {
            url = app.AskServer(Header(1, Strings.UiServerTitle), url, Strings.UiServerHelp)!;
            var code = app.AskCode(Header(2, Strings.UiCodeTitle), Strings.UiCodeHelp, null);
            var child = severalAccounts ? app.AskAccount(Header(3, Strings.UiAccountTitle))
                : ChildAccountChoice.Resolve(app.ChildUserName).Account;

            Header(total, Strings.UiSummaryTitle)();
            Summary(c, url, child);
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

            app.Log.Info($"wizard: install, server {url}, account {child?.Name ?? "(Default profile only)"}");
            var result = app.PairAndApply(() => Theme.Header(c, Strings.UiSetupTitle, Strings.UiInstalling), url, code,
                Strings.UiCodeHelp, (installer, key) => installer.Install(App.Request(url, key, child)));
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
            app.VerifyShort(child);
            Prompts.Continue(c, Strings.UiPressEnter);
            return;
        }
    }

    private static void Summary(IAnsiConsole c, string url, UserAccount? child)
    {
        c.Write(Theme.Facts(new[]
        {
            (Strings.UiSummaryServer, $"[aqua]{Theme.Esc(url)}[/]"),
            (Strings.UiSummaryAccount, child is null ? $"[yellow]{Theme.Esc(Strings.UiSummaryNoAccount)}[/]" : Theme.Esc(child.Name)),
            (Strings.UiSummaryVersion, Theme.Esc(AppVersion.Display)),
        }));
        Theme.Blank(c);
        c.MarkupLine($"[bold]{Theme.Esc(Strings.UiSummaryWhat)}[/]");
        Theme.Bullet(c, Strings.UiSummaryService);
        Theme.Bullet(c, Strings.UiSummaryLock);
        Theme.Bullet(c, Strings.UiSummaryPolicies(child?.Name));
        if (child is null) Theme.Problem(c, CheckLevel.Warn, Strings.UiAccountNone);
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
