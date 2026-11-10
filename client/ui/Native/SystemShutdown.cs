using System.ComponentModel;
using System.Runtime.InteropServices;

namespace Vigil.Ui.Native;

/// <summary>
/// Shuts the PC down from the child's session: ExitWindowsEx, the call the Start menu makes, after enabling
/// SeShutdownPrivilege in vigil_ui's own token (present but disabled in a user token).
///
/// No shutdown.exe (#98): run from vigil_ui it exited without shutting down and without a word (no event 1074),
/// while the same command from the child's cmd worked; its exit code was never read. The direct call needs no
/// child process or environment (D24) and returns the Win32 error, which the lock screen shows.
/// </summary>
public static class SystemShutdown
{
    /// <summary>Starts a power-off. Throws <see cref="Win32Exception"/> when Windows refuses it.</summary>
    public static void PowerOff()
    {
        // Pseudo-handle: vigil_ui's process DACL denies opening itself by PID (CLAUDE.md).
        if (!OpenProcessToken(GetCurrentProcess(), TOKEN_ADJUST_PRIVILEGES | TOKEN_QUERY, out var token))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "OpenProcessToken");
        try
        {
            if (!LookupPrivilegeValue(null, "SeShutdownPrivilege", out var luid))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "LookupPrivilegeValue");
            var tp = new TOKEN_PRIVILEGES { PrivilegeCount = 1, Luid = luid, Attributes = SE_PRIVILEGE_ENABLED };
            // Succeeds even when the privilege is missing; ERROR_NOT_ALL_ASSIGNED tells.
            AdjustTokenPrivileges(token, false, ref tp, 0, IntPtr.Zero, IntPtr.Zero);
            var err = Marshal.GetLastWin32Error();
            if (err != 0) throw new Win32Exception(err, "AdjustTokenPrivileges");
        }
        finally
        {
            CloseHandle(token);
        }

        if (!ExitWindowsEx(EWX_SHUTDOWN | EWX_POWEROFF, SHTDN_REASON_MAJOR_OTHER | SHTDN_REASON_FLAG_PLANNED))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "ExitWindowsEx");
    }

    private const uint TOKEN_ADJUST_PRIVILEGES = 0x0020;
    private const uint TOKEN_QUERY = 0x0008;
    private const uint SE_PRIVILEGE_ENABLED = 0x0002;
    private const uint EWX_SHUTDOWN = 0x0001;
    private const uint EWX_POWEROFF = 0x0008;
    private const uint SHTDN_REASON_MAJOR_OTHER = 0x00000000;
    private const uint SHTDN_REASON_FLAG_PLANNED = 0x80000000;

    [StructLayout(LayoutKind.Sequential)]
    private struct LUID
    {
        public uint LowPart;
        public int HighPart;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TOKEN_PRIVILEGES
    {
        public uint PrivilegeCount;
        public LUID Luid;
        public uint Attributes;
    }

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(IntPtr process, uint desiredAccess, out IntPtr token);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "LookupPrivilegeValueW")]
    private static extern bool LookupPrivilegeValue(string? systemName, string name, out LUID luid);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool AdjustTokenPrivileges(IntPtr token, bool disableAll, ref TOKEN_PRIVILEGES newState,
        uint bufferLength, IntPtr previousState, IntPtr returnLength);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool ExitWindowsEx(uint flags, uint reason);
}
