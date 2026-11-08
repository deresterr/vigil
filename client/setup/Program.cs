using System.Runtime.Versioning;
using System.Text;
using Vigil.Setup;
using Vigil.Setup.Core;
using Vigil.Setup.Native;

[assembly: SupportedOSPlatform("windows")]

// vigil_setup.exe (D22). Parameters as Install.cmd takes them (-Mode verify ...), so README, scripts and
// the CI e2e work with both installers. Without parameters: the wizard and menu (#64); for now, usage.

Console.OutputEncoding = Encoding.UTF8;
int exitCode;
try
{
    exitCode = Run(Cli.Parse(args));
}
catch (CliException ex)
{
    ConsoleReport.Error(ex.Message);
    Console.WriteLine();
    Console.WriteLine(Strings.Usage);
    exitCode = 2;
}
PauseIfOwnWindow();
return exitCode;

static int Run(Cli cli)
{
    switch (cli.Mode)
    {
        case null:
            Console.WriteLine(Strings.Usage);
            return 0;
        case "verify":
            var verify = new Verify(new ConsoleReport(), cli.ChildUserName, AppContext.BaseDirectory);
            ConsoleReport.Title(Strings.VerifyTitle);
            verify.Run();
            ConsoleReport.Summary(Strings.VerifyTitle, Strings.VerifySummary(verify.Pass, verify.Fail, verify.Warn),
                verify.Fail > 0 ? CheckLevel.Fail : verify.Warn > 0 ? CheckLevel.Warn : CheckLevel.Pass);
            return verify.Fail > 0 ? 1 : 0;
        default:
            ConsoleReport.Error(Strings.ModeNotYet(cli.Mode));
            return 2;
    }
}

// Started by a double click (its own console window, which closes on exit): wait for a key. From a
// terminal, a script or CI the console is shared with the caller: no pause.
static void PauseIfOwnWindow()
{
    var processes = new uint[2];
    if (Win32.GetConsoleProcessList(processes, 2) != 1 || Console.IsInputRedirected) return;
    Console.WriteLine();
    Console.WriteLine(Strings.PressAnyKey);
    Console.ReadKey(intercept: true);
}
