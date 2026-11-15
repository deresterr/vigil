namespace Vigil.Setup;

public sealed class CliException(string message) : Exception(message);

/// <summary>
/// The parameter mode: -Mode, -ServerUrl, -PairingCode, -ChildUsername, -CreateChildAccount, -DisableWinRE,
/// -NoUserPolicies (PowerShell style, names case-insensitive). Passwords never in an argument (the process list,
/// CI logs): VIGIL_CHILD_PASSWORD, VIGIL_PARENT_PASSWORD.
/// </summary>
public sealed class Cli
{
    public static readonly string[] Modes = { "install", "uninstall", "update-config", "reinstall", "verify", "update" };

    public string? Mode { get; private set; }
    public string? ServerUrl { get; private set; }
    public string? PairingCode { get; private set; }
    public string? ChildUserName { get; private set; }
    /// <summary>-CreateChildAccount &lt;name&gt;: the standard-account setup with a new account (D30, #160).</summary>
    public string? CreateChildAccount { get; private set; }
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
                case "-createchildaccount": cli.CreateChildAccount = Value(); break;
                case "-disablewinre": cli.DisableWinRE = true; break;
                case "-nouserpolicies": cli.NoUserPolicies = true; break;
                case "-keys": cli.KeysFile = Value(); break;
                default: throw new CliException(Strings.UnknownParameter(name));
            }
        }
        if (cli.CreateChildAccount is not null && cli.ChildUserName is not null)
            throw new CliException(Strings.ChildAccountBothGiven);
        if (cli.CreateChildAccount is not null && cli.Mode is not ("install" or "reinstall"))
            throw new CliException(Strings.CreateChildOnlyInstall);
        return cli;
    }
}
