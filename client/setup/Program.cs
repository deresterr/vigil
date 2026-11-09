using System.Runtime.Versioning;
using System.Text;
using Vigil.Setup;
using Vigil.Setup.Core;

[assembly: SupportedOSPlatform("windows")]

// vigil_setup.exe (D22): the whole client package in one file, the watchdog and vigil_ui appended (#76,
// Core/Package.cs). Without parameters: the wizard (nothing installed) or the menu. With parameters,
// as Install.cmd and vigil_install.ps1 took them (-Mode install -ServerUrl ... -PairingCode ...): for scripts, CI and support.
// `vigil_setup.exe guardian`: the guardian task.

// The task "Vigil Guardian" (SYSTEM, every 5 min, #65): no console work, no log file in %TEMP%, no pause;
// its output is the Event Log.
if (args is [var only] && only.Equals(Installer.GuardianArgument, StringComparison.OrdinalIgnoreCase))
    return Guardian.Run();

Console.OutputEncoding = Encoding.UTF8;
Cli cli;
try { cli = Cli.Parse(args); }
catch (CliException ex)
{
    ConsoleReport.Error(ex.Message);
    Console.WriteLine();
    Console.WriteLine(Strings.Usage);
    ConsoleWindow.PauseIfOwn();
    return 2;
}

using var log = new LogReport();
if (cli.Mode is null)
{
    // The wizard and the menu need a person at a console (or the CI key script); a script without
    // parameters gets the usage instead of a prompt that waits forever.
    if (cli.KeysFile is null && (Console.IsInputRedirected || Console.IsOutputRedirected))
    {
        Console.WriteLine(Strings.Usage);
        return 2;
    }
    return Vigil.Setup.Screens.App.Run(cli, log);
}

int exitCode;
try
{
    exitCode = RunMode(cli, log);
}
catch (SetupException ex)
{
    log.Error(ex);
    ConsoleReport.Summary(Strings.Failed, ex.Message, CheckLevel.Fail);
    exitCode = 1;
}
catch (Exception ex)
{
    log.Error(ex);
    ConsoleReport.Summary(Strings.Failed, ex.Message, CheckLevel.Fail);
    exitCode = 1;
}
if (log.Path is not null) Console.WriteLine(Strings.LogAt(log.Path));
ConsoleWindow.PauseIfOwn();
return exitCode;

static int RunMode(Cli cli, LogReport log)
{
    var report = new TeeReport(new ConsoleReport(), log);
    // The payload appended to this exe (#76); null in the installed copy: install and update then refuse.
    var package = Package.OfThisProgram();
    var installer = new Installer(report, package);
    var title = cli.Mode!.ToUpperInvariant();
    ConsoleReport.Title(title);

    switch (cli.Mode)
    {
        case "verify":
        {
            var verify = new Verify(report, cli.ChildUserName, package);
            verify.Run();
            ConsoleReport.Summary(Strings.VerifyTitle, Strings.VerifySummary(verify.Pass, verify.Fail, verify.Warn),
                verify.Fail > 0 ? CheckLevel.Fail : verify.Warn > 0 ? CheckLevel.Warn : CheckLevel.Pass);
            return verify.Fail > 0 ? 1 : 0;
        }
        case "install":
        {
            var (url, code) = RequireUrlAndCode(cli);
            var key = installer.Pair(url, code);
            installer.Install(Request(cli, url, key, report));
            break;
        }
        case "reinstall":
        {
            // Pairing first: a wrong code leaves the installation as it was.
            var (url, code) = RequireUrlAndCode(cli);
            var key = installer.Pair(url, code);
            installer.Uninstall();
            installer.Install(Request(cli, url, key, report));
            break;
        }
        case "update":
        {
            var url = cli.ServerUrl is { } u ? Normalize(u) : Installer.InstalledServerUrl()
                ?? throw new SetupException(Strings.NothingToUpdate);
            installer.Install(Request(cli, url, null, report));
            break;
        }
        case "update-config":
        {
            if (cli.ServerUrl is null && cli.PairingCode is null) throw new SetupException(Strings.UpdateConfigNeedsParams);
            var url = cli.ServerUrl is { } u ? Normalize(u) : Installer.InstalledServerUrl()
                ?? throw new SetupException(Strings.NotInstalled);
            // Re-pair before stopping anything: a wrong or expired code leaves the installation as it was.
            var key = cli.PairingCode is { } c ? installer.Pair(url, Code(c)) : null;
            installer.UpdateConfig(url, key);
            break;
        }
        case "uninstall":
            if (!installer.Uninstall())
            {
                ConsoleReport.Summary(title, Strings.UninstallIncomplete, CheckLevel.Fail);
                return 1;
            }
            break;
    }
    ConsoleReport.Summary(title, Strings.Done, installer.Warnings > 0 ? CheckLevel.Warn : CheckLevel.Pass);
    return 0;
}

static (string Url, string Code) RequireUrlAndCode(Cli cli)
{
    if (cli.ServerUrl is null || cli.PairingCode is null) throw new SetupException(Strings.InstallNeedsParams);
    var url = Normalize(cli.ServerUrl);
    if (!Pairing.IsHealthy(url)) throw new SetupException(Strings.ServerNotResponding(url));
    return (url, Code(cli.PairingCode));
}

static string Normalize(string raw)
{
    var url = Pairing.NormalizeUrl(raw);
    if (!Pairing.IsValidUrl(url)) throw new SetupException(Strings.InvalidServerUrl(raw));
    return url;
}

static string Code(string raw) => Pairing.NormalizeCode(raw) ?? throw new SetupException(Strings.CodeFormat);

/// <summary>Parameters: the account is resolved, never asked for; several accounts and no -ChildUsername =
/// the Default profile only (as the PowerShell installer did).</summary>
static InstallRequest Request(Cli cli, string url, string? key, IReport report)
{
    var child = ChildAccountChoice.Resolve(cli.ChildUserName);
    if (child.Account is null && cli.ChildUserName is not null) throw new SetupException(child.Problem!);
    if (child.Account is null) report.Check(CheckLevel.Warn, Strings.NoChildAccountForVerify(child.Problem));
    else report.Info(Strings.ChildAccount(child.Account.Name));
    return new InstallRequest
    {
        ServerUrl = url,
        SigningKey = key,
        Child = child.Account,
        ApplyUserPolicies = !cli.NoUserPolicies,
        DisableWinRE = cli.DisableWinRE,
    };
}
