using System.Runtime.InteropServices;
using System.Security.Principal;

namespace Vigil.Shared;

/// <summary>
/// Whether an account is in the local Administrators group (#160, #165), by SID: the group's name is localized
/// ("Administratorzy"). Direct members, which is what a home PC's local and Microsoft accounts are. Its own P/Invoke,
/// as the file is linked into the watchdog and vigil_setup.
/// </summary>
public static class LocalAdmins
{
    /// <returns>null when the group cannot be read.</returns>
    public static bool? Contains(string sid)
    {
        string group;
        try
        {
            group = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null)
                .Translate(typeof(NTAccount)).Value;
        }
        catch (SystemException) { return null; }  // IdentityNotMappedException among them
        group = group[(group.IndexOf('\\') + 1)..];

        var rc = NetLocalGroupGetMembers(null, group, 0, out var buffer, MaxPreferredLength, out var read, out _, IntPtr.Zero);
        try
        {
            if (rc != 0) return null;
            for (var i = 0; i < read; i++)
            {
                // LOCALGROUP_MEMBERS_INFO_0: a single PSID.
                var member = Marshal.ReadIntPtr(buffer, i * IntPtr.Size);
                if (new SecurityIdentifier(member).Value.Equals(sid, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }
        finally
        {
            if (buffer != IntPtr.Zero) NetApiBufferFree(buffer);
        }
    }

    private const int MaxPreferredLength = -1;

    [DllImport("netapi32.dll", CharSet = CharSet.Unicode)]
    private static extern int NetLocalGroupGetMembers(string? serverName, string localGroupName, int level,
        out IntPtr buffer, int preferredMaxLength, out int entriesRead, out int totalEntries, IntPtr resumeHandle);

    [DllImport("netapi32.dll")]
    private static extern int NetApiBufferFree(IntPtr buffer);
}
