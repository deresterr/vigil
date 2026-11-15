using System.Security.Principal;
using Vigil.Shared;

namespace Vigil.Watchdog.Configuration;

/// <summary>
/// The watchdog's configuration, C:\ProgramData\Vigil\vigil.toml (shared/VigilToml.cs): the server address and,
/// in the standard-account setup (D30), the child's account. The signing key is in signing.dat (DPAPI), not here.
/// </summary>
public sealed class VigilConfig
{
    public required string ServerUrl { get; init; }

    /// <summary>The child's account (standard): only its sessions are counted and locked. Null: every session is
    /// the child's (the one-account setup, and installs up to 0.9).</summary>
    public string? ChildSid { get; init; }

    /// <summary>
    /// Reads the file; throws when it is missing or incomplete, so the service fails at start instead of
    /// running half-configured.
    /// </summary>
    public static VigilConfig LoadFromFile(string path)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"{path} does not exist: install with vigil_setup.exe", path);
        }

        var values = VigilToml.Parse(File.ReadAllText(path));

        if (!values.TryGetValue(VigilToml.ServerUrl, out var serverUrl))
        {
            throw new InvalidDataException($"vigil.toml: the key '{VigilToml.ServerUrl}' is missing.");
        }
        if (!Uri.TryCreate(serverUrl, UriKind.Absolute, out _))
        {
            throw new InvalidDataException($"vigil.toml: server_url '{serverUrl}' is not an absolute URI.");
        }

        var childSid = values.GetValueOrDefault(VigilToml.ChildSid);
        if (childSid is not null)
        {
            try { childSid = new SecurityIdentifier(childSid).Value; }
            catch (ArgumentException)
            {
                throw new InvalidDataException($"vigil.toml: child_sid '{childSid}' is not a SID.");
            }
        }

        return new VigilConfig
        {
            ServerUrl = serverUrl.TrimEnd('/'),
            ChildSid = childSid,
        };
    }
}
