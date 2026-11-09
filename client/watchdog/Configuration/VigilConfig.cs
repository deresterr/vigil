using Tomlyn;
using Tomlyn.Model;

namespace Vigil.Watchdog.Configuration;

/// <summary>
/// The watchdog's configuration, C:\ProgramData\Vigil\vigil.toml: the server address only. The signing key
/// is in signing.dat (DPAPI), not here.
/// </summary>
public sealed class VigilConfig
{
    public required string ServerUrl { get; init; }

    /// <summary>
    /// Reads the file; throws when it is missing or incomplete, so the service fails at start instead of
    /// running half-configured.
    /// </summary>
    public static VigilConfig LoadFromFile(string path)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException(
                $"{path} does not exist: install with vigil_setup.exe",
                path);
        }

        var text = File.ReadAllText(path);
        if (!Toml.TryToModel(text, out TomlTable? table, out var diagnostics) || table is null)
        {
            throw new InvalidDataException(
                $"vigil.toml is not valid TOML: {diagnostics}");
        }

        // Other keys are ignored.
        var serverUrl = RequireString(table, "server_url");

        if (!Uri.TryCreate(serverUrl, UriKind.Absolute, out _))
        {
            throw new InvalidDataException(
                $"vigil.toml: server_url '{serverUrl}' is not an absolute URI.");
        }

        return new VigilConfig
        {
            ServerUrl = serverUrl.TrimEnd('/'),
        };
    }

    private static string RequireString(TomlTable t, string key)
    {
        if (!t.TryGetValue(key, out var raw))
        {
            throw new InvalidDataException($"vigil.toml: the key '{key}' is missing.");
        }
        if (raw is not string s)
        {
            throw new InvalidDataException(
                $"vigil.toml: '{key}' must be a string, not {raw?.GetType().Name ?? "null"}.");
        }
        return s;
    }
}
