using System;
using System.IO;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;

namespace Vigil.Watchdog.Lockscreen;

/// <summary>
/// Czyta bypass.flag (zapisany przez vigil_lockscreen po valid emergency password) i mówi
/// watchdogowi czy lockscreen ma być pominięty.
///
/// Flag plik: C:\Users\Public\Vigil\bypass.flag
/// Format: DPAPI-encrypted "valid_until_utc=ISO8601"
/// Skuteczny: 24h od zapisu (timestamp wewnątrz pliku, nie polegamy na FS modification time).
///
/// Bezpieczeństwo:
/// - DPAPI LocalMachine zabezpiecza że ktoś z innej maszyny nie wgra valid flag
/// - Entropy "Vigil-Bypass-v1" — bez znajomości tego stringa (kompilation-time const)
///   nie da się sforge'ować content
/// - Timestamp w środku — stary plik (>24h) jest automatycznie zignorowany
/// </summary>
[SupportedOSPlatform("windows")]
public static class BypassFlagChecker
{
    public const string BypassFlagPath = @"C:\Users\Public\Vigil\bypass.flag";

    // MUSI być zgodne z EmergencyAuth w vigil_lockscreen
    private static readonly byte[] BypassEntropy = Encoding.UTF8.GetBytes("Vigil-Bypass-v1");

    /// <summary>
    /// Sprawdza czy aktywna jest awaryjna bypass flag.
    /// Zwraca true gdy plik istnieje, dekrypcja udała się, timestamp jest w przyszłości.
    /// </summary>
    public static bool IsActive(ILogger? logger = null)
    {
        if (!File.Exists(BypassFlagPath)) return false;

        try
        {
            var encrypted = File.ReadAllBytes(BypassFlagPath);
            var plain = ProtectedData.Unprotect(
                encrypted, BypassEntropy, DataProtectionScope.LocalMachine);
            var payload = Encoding.UTF8.GetString(plain);

            // Format: "valid_until_utc=2026-05-16T20:30:00.0000000+00:00"
            var prefix = "valid_until_utc=";
            if (!payload.StartsWith(prefix, StringComparison.Ordinal))
            {
                logger?.LogWarning("Bypass flag ma nieprawidłowy format payload");
                return false;
            }

            var tsStr = payload.Substring(prefix.Length);
            if (!DateTime.TryParse(tsStr, null,
                System.Globalization.DateTimeStyles.RoundtripKind, out var validUntil))
            {
                logger?.LogWarning("Bypass flag ma nieparsowalny timestamp: {Ts}", tsStr);
                return false;
            }

            // Konwertuj do UTC jeśli timestamp w innej strefie
            if (validUntil.Kind != DateTimeKind.Utc)
            {
                validUntil = validUntil.ToUniversalTime();
            }

            var active = validUntil > DateTime.UtcNow;
            if (active)
            {
                logger?.LogInformation(
                    "Bypass flag aktywna — pomijam lockscreen do {ValidUntil:o}",
                    validUntil);
            }
            else
            {
                logger?.LogInformation(
                    "Bypass flag wygasła ({ValidUntil:o}) — kasuję plik",
                    validUntil);
                try { File.Delete(BypassFlagPath); } catch { /* ignore */ }
            }
            return active;
        }
        catch (CryptographicException ex)
        {
            // DPAPI decrypt fail — najpewniej forge attempt (dziecko wpisało garbage)
            // albo plik z innej maszyny / starsza wersja entropy
            logger?.LogWarning(ex, "Bypass flag — DPAPI decrypt failed, kasuję plik");
            try { File.Delete(BypassFlagPath); } catch { /* ignore */ }
            return false;
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "Bypass flag check failed");
            return false;
        }
    }
}
