namespace Vigil.Setup;

public sealed class CliException(string message) : Exception(message);

/// <summary>
/// The parameters of the old Install.cmd / vigil_install.ps1, unchanged: -Mode, -ServerUrl, -PairingCode,
/// -ChildUsername, -DisableWinRE, -NoUserPolicies. Names are case-insensitive, as in PowerShell.
/// </summary>
public sealed class Cli
{
    public static readonly string[] Modes = { "install", "uninstall", "update-config", "reinstall", "verify", "update" };

    public string? Mode { get; private set; }
    public string? ServerUrl { get; private set; }
    public string? PairingCode { get; private set; }
    public string? ChildUserName { get; private set; }
    public bool DisableWinRE { get; private set; }
    public bool NoUserPolicies { get; private set; }
    /// <summary>-Keys &lt;file&gt;: the wizard and menu driven by a key script (CI e2e only, not in the usage).</summary>
    public string? KeysFile { get; private set; }

    public static Cli Parse(IReadOnlyList<string> args)
    {
        var cli = new Cli();
        for (var i = 0; i < args.Count; i++)
        {
            var name = args[i];
            string Value()
            {
                if (i + 1 >= args.Count || args[i + 1].StartsWith('-')) throw new CliException(Strings.MissingValue(name));
                return args[++i];
            }
            switch (name.ToLowerInvariant())
            {
                case "-mode":
                    var mode = Value().ToLowerInvariant();
                    if (!Modes.Contains(mode)) throw new CliException(Strings.UnknownMode(mode));
                    cli.Mode = mode;
                    break;
                case "-serverurl": cli.ServerUrl = Value(); break;
                case "-pairingcode": cli.PairingCode = Value(); break;
                case "-childusername": cli.ChildUserName = Value(); break;
                case "-disablewinre": cli.DisableWinRE = true; break;
                case "-nouserpolicies": cli.NoUserPolicies = true; break;
                case "-keys": cli.KeysFile = Value(); break;
                default: throw new CliException(Strings.UnknownParameter(name));
            }
        }
        return cli;
    }
}
