using System.Runtime.Versioning;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using System.Text;
using Vigil.Shared;

namespace Vigil.Watchdog.Configuration;

/// <summary>
/// Trzyma signing_key zaszyfrowany przez DPAPI w scope LocalMachine.
///
/// Normalny przepływ (install.ps1): install.ps1 zapisuje signing.dat bezpośrednio
/// przez DPAPI jeszcze przed pierwszym startem serwisu. BootstrapOrLoad() wywołuje
/// wtedy wyłącznie Load() — ścieżka przez env/argv nigdy nie jest używana.
///
/// Fallback bootstrap (ręczny/legacy): jeśli signing.dat nie istnieje, BootstrapOrLoad
/// szuka klucza w --signing-key=... (argv) lub PC_SIGNING_KEY (env), szyfruje i zapisuje.
///
/// LocalMachine scope (nie User) wymagany bo service działa jako SYSTEM, ale jeśli kiedyś
/// chcielibyśmy odczytać z innego konta tej samej maszyny, też się da.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class DpapiKeyStore
{
    private readonly string _filePath;
    private readonly ILogger<DpapiKeyStore> _logger;

    // Optional entropy = "salt" dla DPAPI. Daje dodatkową ochronę gdyby ktoś próbował zaimportować
    // signing.dat na innej maszynie (DPAPI LocalMachine i tak nie pozwoli, ale defense in depth).
    private static readonly byte[] _dpapiEntropy =
        System.Text.Encoding.UTF8.GetBytes(VigilLayout.SigningKeyEntropy);

    public DpapiKeyStore(string filePath, ILogger<DpapiKeyStore> logger)
    {
        _filePath = filePath;
        _logger = logger;
    }

    public bool Exists() => File.Exists(_filePath);

    /// <summary>
    /// Bootstrap: zaszyfruj plain key (string base64 z servera) → zapisz do signing.dat.
    /// Wywoływane jednorazowo przy pierwszym starcie. Idempotentne — nadpisuje jeśli istnieje.
    /// </summary>
    public void Save(string base64Key)
    {
        var keyBytes = DecodeBase64UrlSafe(base64Key);
        if (keyBytes.Length < 16)
        {
            throw new ArgumentException(
                $"signing_key po dekodowaniu ma {keyBytes.Length} bajtów — oczekiwane min. 16. " +
                "Sprawdź czy podałeś prawidłowy klucz z bootstrap logów servera.");
        }

        var encrypted = ProtectedData.Protect(
            keyBytes, _dpapiEntropy, DataProtectionScope.LocalMachine);

        // Zapisz przez temp file + atomic rename, żeby crash w środku zapisu nie zostawił
        // pół-zapisanego pliku.
        var tempPath = _filePath + ".tmp";
        File.WriteAllBytes(tempPath, encrypted);
        File.Move(tempPath, _filePath, overwrite: true);

        // Ustaw bezpieczne ACL na pliku — SYSTEM full, Administrators read.
        RestrictFileAcl(_filePath, _logger);

        _logger.LogInformation("signing.dat zapisany ({Bytes}B encrypted)", encrypted.Length);

        // Nadpisz bytes w pamięci jak najszybciej. Ta gwarancja jest słaba w .NET (GC może mieć kopie),
        // ale lepsze to niż nic.
        Array.Clear(keyBytes, 0, keyBytes.Length);
    }

    /// <summary>Czyta i deszyfruje signing_key. Rzuca jeśli pliku nie ma lub decrypt failed.</summary>
    public byte[] Load()
    {
        if (!File.Exists(_filePath))
        {
            throw new FileNotFoundException(
                $"signing.dat nie istnieje pod {_filePath}. " +
                "Pierwszy start: ustaw env PC_SIGNING_KEY albo --signing-key=...",
                _filePath);
        }

        var encrypted = File.ReadAllBytes(_filePath);
        try
        {
            return ProtectedData.Unprotect(encrypted, _dpapiEntropy, DataProtectionScope.LocalMachine);
        }
        catch (CryptographicException ex)
        {
            // Najczęściej: plik przeniesiony z innej maszyny, lub LocalMachine DPAPI master key zniknął
            // (uszkodzony profil systemu, restore z innego sprzętu).
            throw new CryptographicException(
                "Nie udało się zdeszyfrować signing.dat. Plik mógł zostać przeniesiony z innej maszyny " +
                "lub klucz DPAPI maszyny jest uszkodzony. Usuń signing.dat i uruchom z PC_SIGNING_KEY.",
                ex);
        }
    }

    /// <summary>
    /// Bootstrap helper: pobierz klucz z env/argv jeśli signing.dat nie istnieje, zapisz, zwróć bytes.
    /// </summary>
    public byte[] BootstrapOrLoad(string[] args)
    {
        if (Exists())
        {
            return Load();
        }

        var fromArgs = ExtractFromArgs(args);
        var fromEnv = Environment.GetEnvironmentVariable("PC_SIGNING_KEY");
        var key = fromArgs ?? fromEnv;

        if (string.IsNullOrEmpty(key))
        {
            throw new InvalidOperationException(
                "Pierwszy start: signing.dat nie istnieje, brak też --signing-key=... w argv " +
                "i brak PC_SIGNING_KEY w env. Skopiuj signing_key z bootstrap logów servera.");
        }

        Save(key);
        return Load();
    }

    private static string? ExtractFromArgs(string[] args)
    {
        const string prefix = "--signing-key=";
        foreach (var a in args)
        {
            if (a.StartsWith(prefix, StringComparison.Ordinal))
            {
                return a[prefix.Length..];
            }
        }
        return null;
    }

    private static byte[] DecodeBase64UrlSafe(string s)
    {
        // Format z servera: urlsafe base64 bez paddingu (Python: urlsafe_b64encode().rstrip(b"=")).
        var padded = s.Replace('-', '+').Replace('_', '/');
        switch (padded.Length % 4)
        {
            case 2: padded += "=="; break;
            case 3: padded += "="; break;
        }
        return Convert.FromBase64String(padded);
    }

    /// <summary>SYSTEM full, Administrators read, no inheritance (FileAcls.SecretRules, the same table the
    /// installer and verify use). Shared with TotpSecretStore.</summary>
    internal static void RestrictFileAcl(string path, ILogger logger)
    {
        // A failure (unusual file system) is only logged: the DPAPI file is useless off this machine anyway.
        try
        {
            FileAcls.ProtectSecret(path);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Nie udało się ustawić ACL na {Path} — kontynuuję mimo to", path);
        }
    }
}
