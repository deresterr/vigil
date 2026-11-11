using System.ServiceProcess;
using Spectre.Console;
using Vigil.Setup.Core;
using Vigil.Shared;

namespace Vigil.Setup.Screens;

/// <summary>
/// Vigil installed: its status and a menu in a loop — check · update/repair · change the server address ·
/// re-pair · uninstall · exit. Each action shows its steps, then (after a change) a short verify.
/// </summary>
internal static class MainMenu
{
    private enum Item { Verify, Update, Server, Repair, Uninstall, Exit }

    /// <summary>What the header shows and the menu depends on.</summary>
    private sealed record State(
        string? Installed, string Package, string? PackageProblem, string? ServerUrl, bool Responds,
        ServiceControllerStatus? Service, bool KeyPresent, ChildAccountChoice Child)
    {
        /// <summary>&gt; 0: the package is newer than the installation (an unreadable version counts as older).</summary>
        public int PackageVsInstalled => Installed is null ? 1 : AppVersion.Compare(Package, Installed);
    }

    public static int Run(App app)
    {
        var c = app.C;
        while (true)
        {
            if (!Installer.IsInstalled()) return 0;
            var state = c.Status().Spinner(Theme.SpinnerFor(c)).SpinnerStyle(Theme.Accent)
                .Start(Theme.Esc(Strings.UiStatusChecking), _ => Read(app));

            Theme.Header(c, Strings.UiMenuTitle);
            c.Write(Theme.Facts(Rows(state)));
            Theme.Blank(c);

            var items = new List<Choice<Item>> { new('1', Strings.UiMenuVerify, Item.Verify) };
            if (state.PackageProblem is null)
            {
                var cmp = state.PackageVsInstalled;
                items.Add(new('2', cmp > 0 ? Strings.UiMenuUpdate(state.Package)
                    : cmp == 0 ? Strings.UiMenuRepair : Strings.UiMenuDowngrade(state.Package), Item.Update));
            }
            items.Add(new('3', Strings.UiMenuServer, Item.Server));
            items.Add(new('4', Strings.UiMenuRepair2, Item.Repair));
            items.Add(new('5', Strings.UiMenuUninstall, Item.Uninstall));
            // Digits in order also without "Update" (the payload-less copy from ProgramData); Exit stays 0.
            items = items.Select((it, i) => it.Value == Item.Exit ? it : it with { Key = (char)('1' + i) }).ToList();
            items.Add(new('0', Strings.UiMenuExit, Item.Exit));

            var item = Prompts.Menu(c, items, escapeKey: '0');
            var label = items.First(i => i.Value == item).Label;
            app.Log.Info($"menu: {item}");
            Action header = () => Theme.Header(c, Strings.UiMenuTitle, label);
            switch (item)
            {
                case Item.Exit:
                    return 0;
                case Item.Verify:
                    header();
                    VerifyView.Run(c, app.Log, app.ChildUserName, app.Package).RenderFull(c);
                    break;
                case Item.Update:
                    Update(app, state, header);
                    break;
                case Item.Server:
                    ChangeServer(app, state, header);
                    break;
                case Item.Repair:
                    Repair(app, state, header);
                    break;
                case Item.Uninstall:
                    if (Uninstall(app, header) is { } exitCode) return exitCode;
                    break;
            }
            Prompts.Continue(c, Strings.UiPressEnterMenu);
        }
    }

    private static State Read(App app)
    {
        var url = Installer.InstalledServerUrl();
        return new State(
            App.InstalledVersion(), AppVersion.Display, app.PackageProblem, url,
            url is not null && Pairing.IsHealthy(url), ServiceControl.Status(),
            File.Exists(VigilLayout.PathOf(VigilLayout.SigningFile)), ChildAccountChoice.Resolve(app.ChildUserName));
    }

    private static IEnumerable<(string, string)> Rows(State s)
    {
        string Good(string t) => $"[green]{Theme.Esc(t)}[/]";
        string Bad(string t) => $"[red]{Theme.Esc(t)}[/]";

        yield return (Strings.UiStatusVersion, Theme.Esc(Strings.UiStatusVersionValue(s.Installed ?? Strings.UiVersionUnknown, s.Package)));
        yield return (Strings.UiStatusServer, s.ServerUrl is null ? Bad(Strings.UiStatusNoConfig)
            : $"[{Theme.AccentTag}]{Theme.Esc(s.ServerUrl)}[/] [grey]·[/] " + (s.Responds ? Good(Strings.UiStatusResponds) : Bad(Strings.UiStatusNoAnswer)));
        yield return (Strings.UiStatusService, s.Service switch
        {
            ServiceControllerStatus.Running => Good(Strings.UiServiceRunning),
            ServiceControllerStatus.Stopped => Bad(Strings.UiServiceStopped),
            null => Bad(Strings.UiServiceMissing),
            { } other => $"[yellow]{Theme.Esc(Strings.UiServiceOther(other.ToString()))}[/]",
        });
        yield return (Strings.UiStatusKey, s.KeyPresent ? Good(Strings.UiKeyPresent) : Bad(Strings.UiKeyMissing));
        yield return (Strings.UiStatusAccount, s.Child.Account is { } a ? Theme.Esc(a.Name)
            : $"[yellow]{Theme.Esc(Accounts.Interactive().Count > 1 ? Strings.UiAccountSeveral : Strings.UiAccountNone)}[/]");
        if (s.PackageProblem is { } problem)
            yield return ("", $"[yellow]{Theme.Esc(problem)}[/]");
    }

    // === Actions ===

    /// <summary>Update, repair or downgrade: the package's files over the installation. Without a key (or an
    /// address) the computer needs pairing, and the same action pairs first.</summary>
    private static void Update(App app, State state, Action header)
    {
        var c = app.C;
        if (!state.KeyPresent || state.ServerUrl is null)
        {
            header();
            Theme.Problem(c, CheckLevel.Warn, Strings.UiCodeNeeded);
            Theme.Blank(c);
            Prompts.Continue(c, Strings.UiPressEnter);
            var url = state.Responds ? state.ServerUrl
                : app.AskServer(header, state.ServerUrl, Strings.UiServerHelp, emptyGoesBack: true);
            if (url is null) return;
            var code = app.AskCode(header, Strings.UiCodeHelpRepair, null);
            var child = app.AskAccount(header);
            Finish(app, app.PairAndApply(header, url, code, Strings.UiCodeHelpRepair,
                (installer, key) => installer.Install(App.Request(url, key, child))), child);
            return;
        }

        header();
        Theme.Text(c, Strings.UiConfirmUpdate(state.Installed ?? Strings.UiVersionUnknown, state.Package));
        Theme.Blank(c);
        var go = Prompts.Menu(c, new[]
        {
            new Choice<bool>('1', state.PackageVsInstalled > 0 ? Strings.UiMenuUpdate(state.Package) : Strings.UiMenuRepair, true),
            new Choice<bool>('0', Strings.UiChoiceCancel, false),
        }, escapeKey: '0');
        if (!go) return;
        var account = app.AskAccount(header);
        header();
        var view = new StepsView(c);
        var error = StepsView.Run(c, app.Log, view, r =>
            new Installer(r, app.Package).Install(App.Request(state.ServerUrl, null, account)));
        Finish(app, new(error is null ? PairOutcome.Done : PairOutcome.Failed, view.Warnings), account);
    }

    private static void ChangeServer(App app, State state, Action header)
    {
        var url = app.AskServer(header, state.ServerUrl, Strings.UiServerChangeHelp + " " + Strings.UiEmptyGoesBack,
            emptyGoesBack: true);
        if (url is null) return;
        header();
        var view = new StepsView(app.C);
        var error = StepsView.Run(app.C, app.Log, view, r => new Installer(r, app.Package).UpdateConfig(url, null));
        Finish(app, new(error is null ? PairOutcome.Done : PairOutcome.Failed, view.Warnings), state.Child.Account);
    }

    /// <summary>A new key from a code; on an installation without the service it installs again with it.</summary>
    private static void Repair(App app, State state, Action header)
    {
        var url = state.Responds ? state.ServerUrl
            : app.AskServer(header, state.ServerUrl, Strings.UiServerHelp + " " + Strings.UiEmptyGoesBack, emptyGoesBack: true);
        if (url is null) return;
        var code = app.AskCode(header, Strings.UiCodeHelpRepair, null);
        var child = state.Child.Account;
        Finish(app, app.PairAndApply(header, url, code, Strings.UiCodeHelpRepair, (installer, key) =>
        {
            if (ServiceControl.Status() is not null && Directory.Exists(VigilLayout.InstallDir))
                installer.UpdateConfig(url, key);
            else
                installer.Install(App.Request(url, key, child));
        }), child);
    }

    /// <returns>The exit code once uninstalled; null when cancelled.</returns>
    private static int? Uninstall(App app, Action header)
    {
        var c = app.C;
        header();
        Theme.Text(c, Strings.UiConfirmUninstall);
        Theme.Blank(c);
        var go = Prompts.Menu(c, new[]
        {
            new Choice<bool>('1', Strings.UiMenuUninstall, true),
            new Choice<bool>('0', Strings.UiChoiceCancel, false),
        }, escapeKey: '0');
        if (!go) return null;

        header();
        var view = new StepsView(c);
        var removed = false;
        var error = StepsView.Run(c, app.Log, view, r => removed = new Installer(r, app.Package).Uninstall());
        c.WriteLine();
        if (error is null && removed)
            c.MarkupLine($"{Theme.Mark(c, CheckLevel.Pass)} [green bold]{Theme.Esc(Strings.UiUninstalled)}[/]");
        else
        {
            Theme.Problem(c, CheckLevel.Fail, Strings.UiUninstallLeft);
            app.ShowLogPath();
        }
        app.VerifyShort(null);
        Prompts.Continue(c, Strings.UiPressEnterExit);
        return error is null && removed ? 0 : 1;
    }

    /// <summary>The outcome line of a change and, when it went through, the short verify.</summary>
    private static void Finish(App app, PairResult result, UserAccount? child)
    {
        var c = app.C;
        if (result.Outcome is PairOutcome.Cancelled or PairOutcome.OtherServer) return;
        c.WriteLine();
        if (result.Outcome == PairOutcome.Failed)
        {
            Theme.Problem(c, CheckLevel.Fail, Strings.UiActionFailed);
            app.ShowLogPath();
            return;
        }
        if (result.Warnings > 0) Theme.Problem(c, CheckLevel.Warn, Strings.UiActionDoneWarnings);
        else c.MarkupLine($"{Theme.Mark(c, CheckLevel.Pass)} [green bold]{Theme.Esc(Strings.UiActionDone)}[/]");
        app.VerifyShort(child);
    }
}
