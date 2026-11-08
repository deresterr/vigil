using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;

namespace Vigil.Shared;

/// <summary>
/// File system permissions of an installation, written by the installer and the watchdog and checked by
/// verify from the same tables.
///
/// The folder: protected, SYSTEM full, Administrators modify, Authenticated Users read+execute (the child
/// too: SYSTEM starts vigil_ui.exe with the child's token). Below it: inherited only, except the DPAPI
/// secrets: protected, SYSTEM full, Administrators read. LocalMachine DPAPI is decryptable by any local
/// account, so the file ACL is what keeps a secret from a non-elevated process. Friction: an elevated
/// admin can still read it (CLAUDE.md, threat model).
/// </summary>
[SupportedOSPlatform("windows")]
public static class FileAcls
{
    public static readonly AclRule[] FolderRules =
    {
        new(WellKnownSidType.LocalSystemSid, FileSystemRights.FullControl),
        new(WellKnownSidType.BuiltinAdministratorsSid, FileSystemRights.Modify),
        new(WellKnownSidType.AuthenticatedUserSid, FileSystemRights.ReadAndExecute),
    };

    public static readonly AclRule[] SecretRules =
    {
        new(WellKnownSidType.LocalSystemSid, FileSystemRights.FullControl),
        new(WellKnownSidType.BuiltinAdministratorsSid, FileSystemRights.Read),
    };

    /// <summary>Protected ACL with exactly <see cref="SecretRules"/> on one file.</summary>
    public static void ProtectSecret(string path)
    {
        var sec = new FileSecurity();
        sec.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        foreach (var rule in SecretRules)
            sec.AddAccessRule(new FileSystemAccessRule(rule.Sid, rule.Rights, AccessControlType.Allow));
        new FileInfo(path).SetAccessControl(sec);
    }

    /// <summary>
    /// True when the explicit rules are exactly <paramref name="want"/>, all Allow (Synchronize ignored,
    /// Windows adds it on its own). <paramref name="diff"/> lists the differences.
    /// </summary>
    public static bool HasExactRules(FileSystemSecurity sec, IReadOnlyCollection<AclRule> want, out string diff)
    {
        const int sync = (int)FileSystemRights.Synchronize;
        var found = new List<string>();
        var differences = new List<string>();
        foreach (FileSystemAccessRule rule in sec.GetAccessRules(includeExplicit: true, includeInherited: false,
                     typeof(SecurityIdentifier)))
        {
            var sid = (SecurityIdentifier)rule.IdentityReference;
            found.Add(sid.Value);
            var expected = want.FirstOrDefault(w => w.Sid == sid);
            if (expected is null || rule.AccessControlType != AccessControlType.Allow
                || ((int)rule.FileSystemRights & ~sync) != ((int)expected.Rights & ~sync))
                differences.Add($"{sid.Value}={rule.AccessControlType}:{rule.FileSystemRights}");
        }
        differences.AddRange(want.Where(w => !found.Contains(w.Sid.Value)).Select(w => $"missing {w.Sid.Value}"));
        diff = string.Join("; ", differences);
        return differences.Count == 0;
    }
}

[SupportedOSPlatform("windows")]
public sealed record AclRule(SecurityIdentifier Sid, FileSystemRights Rights)
{
    public AclRule(WellKnownSidType type, FileSystemRights rights) : this(new SecurityIdentifier(type, null), rights) { }
}
