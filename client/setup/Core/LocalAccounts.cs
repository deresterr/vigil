using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32;
using Vigil.Setup.Native;
using Vigil.Shared;

namespace Vigil.Setup.Core;

/// <summary>The child's own account (D30, #160): a local standard account the installer creates, the parent's
/// account's password, automatic sign-in.</summary>
public static class LocalAccounts
{
    /// <summary>Not allowed in a local user name; at most 20 characters.</summary>
    private static readonly char[] Forbidden = "\"/\\[]:;|=,+*?<>@".ToCharArray();
    private const string Winlogon = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon";

    /// <summary>Why <paramref name="name"/> cannot be the new account's name; null when it can.</summary>
    public static string? NameProblem(string name)
    {
        if (name.Length is 0 or > 20 || name.IndexOfAny(Forbidden) >= 0 || name.Trim('.', ' ').Length == 0
            || name.Any(char.IsControl))
            return Strings.AccountNameInvalid;
        return Accounts.SidOf(name) is not null ? Strings.AccountNameTaken(name) : null;
    }

    /// <summary>A local standard account with its profile (NTUSER.DAT, so the policies have a hive to go to).</summary>
    public static UserAccount CreateChild(string name, string? password)
    {
        var blank = string.IsNullOrEmpty(password);
        var info = new Win32.USER_INFO_1
        {
            usri1_name = name,
            usri1_password = password ?? "",
            usri1_priv = Win32.USER_PRIV_USER,
            usri1_flags = Win32.UF_SCRIPT | Win32.UF_DONT_EXPIRE_PASSWD | (blank ? Win32.UF_PASSWD_NOTREQD : 0),
        };
        var rc = Win32.NetUserAdd(null, 1, ref info, out _);
        if (rc != 0) throw new SetupException(Strings.AccountCreateFailed(name, rc));
        var sid = Accounts.SidOf(name) ?? throw new SetupException(Strings.AccountCreateFailed(name, -1));

        // Users by SID (the group's name is localized); NetUserAdd usually put it there already.
        var users = new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null).Translate(typeof(NTAccount)).Value;
        var member = new Win32.LOCALGROUP_MEMBERS_INFO_3 { lgrmi3_domainandname = $"{Environment.MachineName}\\{name}" };
        rc = Win32.NetLocalGroupAddMembers(null, users[(users.IndexOf('\\') + 1)..], 3, ref member, 1);
        if (rc is not 0 and not Win32.ERROR_MEMBER_IN_ALIAS) throw new SetupException(Strings.AccountCreateFailed(name, rc));

        var path = new char[260];
        var hr = Win32.CreateProfile(sid, name, path, (uint)path.Length);
        if (hr is not 0 and not Win32.HRESULT_ALREADY_EXISTS)
            throw new SetupException(Strings.ProfileCreateFailed(name, hr));
        return Accounts.Interactive().FirstOrDefault(a => a.Sid == sid)
               ?? throw new SetupException(Strings.ProfileCreateFailed(name, hr));
    }

    /// <summary>The account this program runs as (elevated): in the one-account PC, the one that becomes the
    /// parent's. (Name without the computer, SID.)</summary>
    public static (string Name, string Sid) Current()
    {
        using var id = WindowsIdentity.GetCurrent();
        var name = id.Name;
        return (name[(name.IndexOf('\\') + 1)..], id.User!.Value);
    }

    /// <summary>
    /// Whether the account signs in without a password: a sign-in with an empty one succeeds, or is refused only
    /// because blank passwords are limited to the console (1327). Asked once, for the parent's account at install:
    /// every try with a wrong password counts towards the account lockout threshold, so verify never asks.
    /// </summary>
    public static bool HasBlankPassword(string userName)
    {
        if (Win32.LogonUserW(userName, ".", "", Win32.LOGON32_LOGON_INTERACTIVE, Win32.LOGON32_PROVIDER_DEFAULT,
                out var token))
        {
            token.Dispose();
            return true;
        }
        var error = Marshal.GetLastWin32Error();
        token.Dispose();
        return error == Win32.ERROR_ACCOUNT_RESTRICTION;
    }

    /// <summary>Sets the first password of an account that has none, as a change by the user (not a reset).</summary>
    public static void SetFirstPassword(string userName, string password)
    {
        var rc = Win32.NetUserChangePassword(null, userName, "", password);
        if (rc != 0) throw new SetupException(Strings.PasswordSetFailed(userName, rc));
    }

    /// <summary>The account Windows signs in to by itself (Winlogon AutoAdminLogon), or null.</summary>
    public static string? AutoLogonUser()
    {
        using var key = Registry.LocalMachine.OpenSubKey(Winlogon);
        if (key?.GetValue("AutoAdminLogon") as string != "1") return null;
        return key.GetValue("DefaultUserName") as string ?? "";
    }

    public static void DisableAutoLogon()
    {
        using var key = Registry.LocalMachine.OpenSubKey(Winlogon, writable: true);
        key?.SetValue("AutoAdminLogon", "0", RegistryValueKind.String);
        // A password kept in plain text for it, if any.
        key?.DeleteValue("DefaultPassword", throwOnMissingValue: false);
    }

    public static bool IsAdministrator(string sid) => LocalAdmins.Contains(sid) == true;
}
