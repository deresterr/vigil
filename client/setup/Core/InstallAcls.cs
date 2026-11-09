using System.Security.AccessControl;
using System.Security.Principal;
using Vigil.Shared;

namespace Vigil.Setup.Core;

/// <summary>
/// File system permissions of C:\ProgramData\Vigil during and after an install (tables in FileAcls).
/// The locked-down state keeps administrators from rewriting the secrets, so every write starts with
/// <see cref="Reclaim"/>; <see cref="LockDown"/> restores the state verify expects. Ownership goes to
/// Administrators (as takeown.exe did): the owner may always rewrite the ACL.
/// </summary>
internal static class InstallAcls
{
    private static readonly SecurityIdentifier Admins = new(WellKnownSidType.BuiltinAdministratorsSid, null);
    private static readonly SecurityIdentifier LocalSystem = new(WellKnownSidType.LocalSystemSid, null);

    /// <summary>
    /// Makes everything under the folder writable and deletable for administrators: folder ACL as installed,
    /// files inherited only, secrets protected with SYSTEM + Administrators full (never readable by users,
    /// not even between Reclaim and LockDown).
    /// </summary>
    public static void Reclaim(string dir)
    {
        if (!Directory.Exists(dir)) return;
        Privileges.EnableOwnershipChange();
        SetFolder(dir);
        foreach (var item in Items(dir))
        {
            TakeOwnership(item);
            if (IsSecret(item)) SetProtected(item, (LocalSystem, FileSystemRights.FullControl), (Admins, FileSystemRights.FullControl));
            else SetInheritedOnly(item);
        }
    }

    /// <summary>Folder protected (SYSTEM full, Administrators modify, Authenticated Users read+execute),
    /// everything below inherited only, secrets SYSTEM full + Administrators read.</summary>
    public static void LockDown(string dir)
    {
        Privileges.EnableOwnershipChange();
        SetFolder(dir);
        foreach (var item in Items(dir))
        {
            TakeOwnership(item);
            if (IsSecret(item)) FileAcls.ProtectSecret(item.FullName);
            else SetInheritedOnly(item);
        }
    }

    /// <summary>Deletes the folder whatever its ACLs say (after Reclaim everything is deletable).</summary>
    public static void DeleteTree(string dir)
    {
        if (!Directory.Exists(dir)) return;
        Reclaim(dir);
        foreach (var item in Items(dir)) item.Attributes = FileAttributes.Normal;
        Directory.Delete(dir, recursive: true);
    }

    private static List<FileSystemInfo> Items(string dir) =>
        new DirectoryInfo(dir).EnumerateFileSystemInfos("*",
            new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = 0, IgnoreInaccessible = false }).ToList();

    private static bool IsSecret(FileSystemInfo item) =>
        item is FileInfo && VigilLayout.SecretFiles.Contains(item.Name, StringComparer.OrdinalIgnoreCase);

    private static void SetFolder(string dir)
    {
        var info = new DirectoryInfo(dir);
        TakeOwnership(info);
        var sec = new DirectorySecurity();
        sec.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        foreach (var rule in FileAcls.FolderRules)
            sec.AddAccessRule(new FileSystemAccessRule(rule.Sid, rule.Rights,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        info.SetAccessControl(sec);
    }

    private static void TakeOwnership(FileSystemInfo item)
    {
        if (item is FileInfo f)
        {
            var sec = new FileSecurity();
            sec.SetOwner(Admins);
            f.SetAccessControl(sec);
        }
        else
        {
            var sec = new DirectorySecurity();
            sec.SetOwner(Admins);
            ((DirectoryInfo)item).SetAccessControl(sec);
        }
    }

    /// <summary>No explicit rules, inheritance on: exactly what the folder grants.</summary>
    private static void SetInheritedOnly(FileSystemInfo item)
    {
        if (item is FileInfo f)
        {
            var sec = new FileSecurity();
            sec.SetAccessRuleProtection(isProtected: false, preserveInheritance: false);
            f.SetAccessControl(sec);
        }
        else
        {
            var sec = new DirectorySecurity();
            sec.SetAccessRuleProtection(isProtected: false, preserveInheritance: false);
            ((DirectoryInfo)item).SetAccessControl(sec);
        }
    }

    private static void SetProtected(FileSystemInfo item, params (SecurityIdentifier Sid, FileSystemRights Rights)[] rules)
    {
        var sec = new FileSecurity();
        sec.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        foreach (var (sid, rights) in rules)
            sec.AddAccessRule(new FileSystemAccessRule(sid, rights, AccessControlType.Allow));
        ((FileInfo)item).SetAccessControl(sec);
    }
}
