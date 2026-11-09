using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using Vigil.Shared;

namespace Vigil.Watchdog.Configuration;

/// <summary>
/// The device's signing key in signing.dat: DPAPI LocalMachine (the service runs as SYSTEM) with the
/// entropy <see cref="VigilLayout.SigningKeyEntropy"/>. vigil_setup writes it after pairing; the watchdog
/// only reads it.
/// </summary>
[SupportedOSPlatform("windows")]
public static class DpapiKeyStore
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes(VigilLayout.SigningKeyEntropy);

    /// <summary>The decrypted key. Throws when the file is missing or does not decrypt.</summary>
    public static byte[] Load(string path)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException($"{path} does not exist: pair this PC with vigil_setup.exe", path);
        try
        {
            return ProtectedData.Unprotect(File.ReadAllBytes(path), Entropy, DataProtectionScope.LocalMachine);
        }
        catch (CryptographicException ex)
        {
            // Usually a file copied from another machine, or a damaged machine DPAPI key.
            throw new CryptographicException(
                $"{path} does not decrypt (copied from another PC, or the machine's DPAPI key is damaged): " +
                "pair this PC again with vigil_setup.exe", ex);
        }
    }

    /// <summary>SYSTEM full, Administrators read, no inheritance (FileAcls.SecretRules, the same table the
    /// installer and verify use). Used by TotpSecretStore.</summary>
    internal static void RestrictFileAcl(string path, ILogger logger)
    {
        // A failure (unusual file system) is only logged: the DPAPI file is useless off this machine anyway.
        try
        {
            FileAcls.ProtectSecret(path);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not set the ACL of {Path}; continuing", path);
        }
    }
}
