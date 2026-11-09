using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using Vigil.Setup.Native;

namespace Vigil.Setup.Core;

public sealed record UserAccount(string Sid, string Name, string ProfilePath)
{
    public string HiveFile => Path.Combine(ProfilePath, "NTUSER.DAT");
}

/// <summary>
/// User accounts with a profile on this PC. The child's account is the only interactive one (D12), so the
/// installer finds it without asking.
/// </summary>
public static class Accounts
{
    private const string ProfileList = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList";
    private static readonly Regex UserSid = new(@"^S-1-5-21-[\d-]+-(\d+)$");

    /// <summary>Built-in Administrator, Guest, DefaultAccount, WDAGUtilityAccount.</summary>
    private static readonly int[] BuiltInRids = { 500, 501, 503, 504 };

    /// <summary>
    /// S-1-5-21-* in ProfileList minus the built-ins by RID and disabled local accounts (a leftover
    /// defaultuser0 from setup), with an NTUSER.DAT on disk. By SID, never by name: Polish Windows calls Guest
    /// "Gość", and a Microsoft account's profile folder is not its user name.
    /// </summary>
    public static List<UserAccount> Interactive()
    {
        var result = new List<UserAccount>();
        using var list = Registry.LocalMachine.OpenSubKey(ProfileList);
        if (list is null) return result;
        foreach (var sid in list.GetSubKeyNames())
        {
            var m = UserSid.Match(sid);
            if (!m.Success || BuiltInRids.Contains(int.Parse(m.Groups[1].Value))) continue;
            using var key = list.OpenSubKey(sid);
            if (key?.GetValue("ProfileImagePath") is not string profile) continue;
            profile = Environment.ExpandEnvironmentVariables(profile);
            if (!File.Exists(Path.Combine(profile, "NTUSER.DAT"))) continue;
            var name = NameOf(sid);
            if (IsDisabledLocalAccount(name)) continue;
            result.Add(new UserAccount(sid, name, profile));
        }
        return result;
    }

    public static string? SidOf(string userName)
    {
        try { return ((SecurityIdentifier)new NTAccount(userName).Translate(typeof(SecurityIdentifier))).Value; }
        catch (Exception) { return null; }
    }

    public static string NameOf(string sid)
    {
        try { return new SecurityIdentifier(sid).Translate(typeof(NTAccount)).Value; }
        catch (Exception) { return sid; }
    }

    /// <summary>A local account (COMPUTER\name) with "account disabled". Others (domain, unknown): false.</summary>
    private static bool IsDisabledLocalAccount(string qualifiedName)
    {
        var slash = qualifiedName.IndexOf('\\');
        if (slash < 0 || !qualifiedName[..slash].Equals(Environment.MachineName, StringComparison.OrdinalIgnoreCase))
            return false;
        if (Win32.NetUserGetInfo(null, qualifiedName[(slash + 1)..], 1, out var buffer) != 0) return false;
        try { return (Marshal.PtrToStructure<Win32.USER_INFO_1>(buffer).usri1_flags & Win32.UF_ACCOUNTDISABLE) != 0; }
        finally { Win32.NetApiBufferFree(buffer); }
    }
}

/// <summary>The account the per-user policies go to: given by name, or the only interactive one.</summary>
public sealed record ChildAccountChoice(UserAccount? Account, string? Problem)
{
    /// <summary>Without asking (verify, parameter mode). A name that does not resolve is a problem.</summary>
    public static ChildAccountChoice Resolve(string? userName)
    {
        var accounts = Accounts.Interactive();
        if (!string.IsNullOrEmpty(userName))
        {
            var sid = Accounts.SidOf(userName);
            var match = accounts.FirstOrDefault(a => a.Sid == sid);
            return match is not null ? new(match, null) : new(null, Strings.AccountNotFound(userName));
        }
        return accounts.Count switch
        {
            1 => new(accounts[0], null),
            0 => new(null, Strings.NoUserAccount),
            _ => new(null, Strings.SeveralAccounts),
        };
    }
}
