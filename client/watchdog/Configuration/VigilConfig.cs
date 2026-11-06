using Tomlyn;
using Tomlyn.Model;

namespace Vigil.Watchdog.Configuration;

/// <summary>
/// Konfiguracja watchdoga wczytywana z C:\ProgramData\Vigil\vigil.toml.
///
/// signing_key NIE jest tu — siedzi w DPAPI (signing.dat). Tylko bootstrapping
/// pierwszego startu czyta signing_key z env/argv i zapisuje do DPAPI.
/// </summary>
public sealed class VigilConfig
{
    public required string ServerUrl { get; init; }

    /// <summary>
    /// Wczytuje TOML z podanej ścieżki. Rzuca jeśli plik nie istnieje lub brakuje wymaganych pól —
    /// watchdog fail-fast na startup zamiast działać z niepełną konfiguracją.
    /// </summary>
    public static VigilConfig LoadFromFile(string path)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException(
                $"vigil.toml nie istnieje pod ścieżką {path}. " +
                "Uruchom installer (install.ps1 --install) z wymaganymi parametrami.",
                path);
        }

        var text = File.ReadAllText(path);
        if (!Toml.TryToModel(text, out TomlTable? table, out var diagnostics) || table is null)
        {
            throw new InvalidDataException(
                $"vigil.toml nie parsuje się jako TOML: {diagnostics}");
        }

        // Extra keys (e.g. bearer_token from protocol v1 installs) are ignored.
        var serverUrl = RequireString(table, "server_url");

        // Walidacja: nie wpuszczamy obviously-broken wartości
        if (!Uri.TryCreate(serverUrl, UriKind.Absolute, out _))
        {
            throw new InvalidDataException(
                $"server_url '{serverUrl}' nie jest prawidłowym URI.");
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
            throw new InvalidDataException($"vigil.toml: brakuje wymaganego klucza '{key}'.");
        }
        if (raw is not string s)
        {
            throw new InvalidDataException(
                $"vigil.toml: klucz '{key}' musi być stringiem, jest {raw?.GetType().Name ?? "null"}.");
        }
        return s;
    }
}
