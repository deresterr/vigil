using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using Vigil.Setup.Native;

namespace Vigil.Setup.Core;

/// <summary>Privileges an elevated administrator has but which are off by default.</summary>
internal static class Privileges
{
    /// <summary>
    /// SeTakeOwnership + SeRestore: become the owner of a file whose ACL shuts administrators out (a secret
    /// written by the watchdog, a tampered file) and so be allowed to rewrite its ACL. What takeown.exe does.
    /// </summary>
    public static void EnableOwnershipChange()
    {
        foreach (var name in new[] { "SeTakeOwnershipPrivilege", "SeRestorePrivilege" })
            Enable(name);
    }

    private static void Enable(string name)
    {
        if (!Win32.OpenProcessToken(Win32.GetCurrentProcess(), Win32.TOKEN_ADJUST_PRIVILEGES | Win32.TOKEN_QUERY,
                out SafeAccessTokenHandle token))
            throw new Win32Exception();
        using (token)
        {
            if (!Win32.LookupPrivilegeValueW(null, name, out var luid)) throw new Win32Exception();
            var tp = new Win32.TOKEN_PRIVILEGES { PrivilegeCount = 1, Luid = luid, Attributes = Win32.SE_PRIVILEGE_ENABLED };
            // Returns true also when the privilege is not held; ERROR_NOT_ALL_ASSIGNED (1300) says so.
            if (!Win32.AdjustTokenPrivileges(token, false, ref tp, 0, IntPtr.Zero, IntPtr.Zero)
                || Marshal.GetLastWin32Error() == 1300)
                throw new SetupException(Strings.PrivilegeMissing(name));
        }
    }
}
