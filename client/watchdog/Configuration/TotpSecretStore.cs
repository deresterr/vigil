using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;

namespace Vigil.Watchdog.Configuration;

/// <summary>
/// The parent's TOTP secret for offline unlock (#9), in C:\ProgramData\Vigil\totp.dat
/// (DPAPI LocalMachine, entropy "Vigil-Totp-v1", SYSTEM full / Administrators read).
/// Delivered by the server in a signed heartbeat response whenever our fingerprint differs.
/// Friction only: an admin can read it (see CLAUDE.md, threat model).
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class TotpSecretStore
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("Vigil-Totp-v1");

    private readonly string _path;
    private readonly ILogger<TotpSecretStore> _logger;
    private readonly object _gate = new();
    private string? _secret;
    private bool _loaded;

    public TotpSecretStore(string path, ILogger<TotpSecretStore> logger)
    {
        _path = path;
        _logger = logger;
    }

    /// <summary>Base32 secret, or null if the server has not sent one yet.</summary>
    public string? Secret
    {
        get
        {
            lock (_gate)
            {
                if (!_loaded) { _secret = TryLoad(); _loaded = true; }
                return _secret;
            }
        }
    }

    /// <summary>Same as server/app/offline.py totp_fingerprint(): first 16 hex chars of SHA-256.</summary>
    public string Fingerprint
    {
        get
        {
            var s = Secret;
            if (s is null) return "";
            return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(s)))[..16].ToLowerInvariant();
        }
    }

    public void Save(string secret)
    {
        lock (_gate)
        {
            var encrypted = ProtectedData.Protect(Encoding.UTF8.GetBytes(secret), Entropy, DataProtectionScope.LocalMachine);
            var tmp = _path + ".tmp";
            File.WriteAllBytes(tmp, encrypted);
            File.Move(tmp, _path, overwrite: true);
            DpapiKeyStore.RestrictFileAcl(_path, _logger);
            _secret = secret;
            _loaded = true;
        }
        _logger.LogInformation("TOTP secret for offline unlock updated");
    }

    private string? TryLoad()
    {
        if (!File.Exists(_path)) return null;
        try
        {
            var plain = ProtectedData.Unprotect(File.ReadAllBytes(_path), Entropy, DataProtectionScope.LocalMachine);
            return Encoding.UTF8.GetString(plain);
        }
        catch (Exception ex)
        {
            // Treated as "no secret": the next heartbeat brings it again.
            _logger.LogWarning(ex, "totp.dat unreadable — waiting for the server to resend the secret");
            return null;
        }
    }
}
