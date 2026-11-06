using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace Vigil.Lockscreen.Native;

/// <summary>
/// Awaryjne odblokowanie lockscreen lokalnie (bez watchdoga/serwera).
///
/// Scenariusz: skasowany kontener, padła sieć, watchdog crashował — lockscreen wisi
/// na ekranie "server_unreachable" lub innym, brak sposobu zdjąć blokadę normalnymi
/// kanałami. Mateusz wpisuje emergency password (ukryty hotkey Ctrl+Shift+Alt+E),
/// lockscreen weryfikuje LOKALNIE (bez sieci), pisze bypass flag i sam się zamyka.
/// Watchdog widzi flagę i nie spawnuje lockscreen przez kolejne 24h.
///
/// Pliki:
/// - emergency.dat (C:\ProgramData\Vigil\): DPAPI-encrypted plaintext password,
///   zapisany przez installer. ACL pozwala child user read.
/// - bypass.flag (C:\Users\Public\Vigil\): DPAPI-encrypted timestamp ważności,
///   tworzony przez lockscreen po valid emergency, czytany przez watchdog.
///   Public folder writable dla każdego user, ale DPAPI LocalMachine + entropy
///   zabezpieczają przed forge (dziecko nie wygeneruje valid flag bez emergency password).
/// </summary>
public static class EmergencyAuth
{
    // Stałe ścieżki — muszą być zgodne z install.ps1 i Worker.cs (watchdog side)
    public const string EmergencyDatPath = @"C:\ProgramData\Vigil\emergency.dat";
    public const string BypassFlagPath = @"C:\Users\Public\Vigil\bypass.flag";

    // Entropy — używane przez DPAPI Protect/Unprotect. MUSI być identyczne we wszystkich
    // miejscach (installer, lockscreen, watchdog). Zmiana = stare pliki nie odszyfrują się.
    private static readonly byte[] EmergencyEntropy = Encoding.UTF8.GetBytes("Vigil-Emergency-v1");
    private static readonly byte[] BypassEntropy = Encoding.UTF8.GetBytes("Vigil-Bypass-v1");

    /// <summary>
    /// Sprawdza czy emergency password jest skonfigurowany w tej instalacji
    /// (czy plik emergency.dat istnieje i jest czytelny).
    /// </summary>
    public static bool IsConfigured()
    {
        return File.Exists(EmergencyDatPath);
    }

    /// <summary>
    /// Weryfikuje wpisane hasło względem zaszyfrowanego emergency.dat.
    /// Zwraca false dla pustego inputu / brakującego pliku / dpapi error / niezgodności.
    /// </summary>
    public static bool TryVerify(string input)
    {
        if (string.IsNullOrEmpty(input)) return false;
        if (!File.Exists(EmergencyDatPath)) return false;

        try
        {
            var encrypted = File.ReadAllBytes(EmergencyDatPath);
            var plain = ProtectedData.Unprotect(
                encrypted, EmergencyEntropy, DataProtectionScope.LocalMachine);
            try
            {
                // Constant-time compare żeby uniknąć timing-attack (przesadne dla 8-12 latka,
                // ale taniej niż się zastanawiać)
                var inputBytes = Encoding.UTF8.GetBytes(input);
                var match = CryptographicOperations.FixedTimeEquals(inputBytes, plain);
                CryptographicOperations.ZeroMemory(inputBytes);
                return match;
            }
            finally
            {
                CryptographicOperations.ZeroMemory(plain);
            }
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Tworzy bypass flag w C:\Users\Public\Vigil\ z timestamp "valid until".
    /// Watchdog czyta plik co tick i jeśli timestamp jest w przyszłości — pomija lockscreen.
    ///
    /// validHours: ile godzin flag ma być aktywna (domyślnie 24h — wystarczy żeby Mateusz
    /// naprawił to co się zepsuło).
    /// </summary>
    public static void WriteBypassFlag(int validHours = 24)
    {
        var dir = Path.GetDirectoryName(BypassFlagPath)!;
        Directory.CreateDirectory(dir);  // Public\Vigil\

        var validUntil = DateTime.UtcNow.AddHours(validHours);
        // Format: "valid_until_utc=2026-05-16T20:30:00Z"
        // Watchdog parsuje to po DPAPI decrypt. Format prosty, łatwo czytelny w debug.
        var payload = $"valid_until_utc={validUntil:o}";
        var payloadBytes = Encoding.UTF8.GetBytes(payload);
        var encrypted = ProtectedData.Protect(
            payloadBytes, BypassEntropy, DataProtectionScope.LocalMachine);
        File.WriteAllBytes(BypassFlagPath, encrypted);
    }
}
