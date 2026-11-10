using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Vigil.Setup.Native;

/// <summary>P/Invoke for vigil_setup, in one file for auditing.</summary>
internal static class Win32
{
    // === Service control manager ===

    public const uint SC_MANAGER_CONNECT = 0x0001;
    public const uint SERVICE_CHANGE_CONFIG = 0x0002;
    public const uint SERVICE_CONFIG_DELAYED_AUTO_START_INFO = 3;
    public const uint READ_CONTROL = 0x00020000;
    public const uint DACL_SECURITY_INFORMATION = 0x00000004;

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern IntPtr OpenSCManagerW(string? machineName, string? databaseName, uint desiredAccess);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern IntPtr OpenServiceW(IntPtr scManager, string serviceName, uint desiredAccess);

    [DllImport("advapi32.dll", SetLastError = true)]
    public static extern bool QueryServiceObjectSecurity(IntPtr service, uint securityInformation,
        byte[]? securityDescriptor, uint bufferSize, out uint bytesNeeded);

    /// <summary>SERVICE_CONFIG_DELAYED_AUTO_START_INFO: the struct is a single BOOL.</summary>
    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern bool ChangeServiceConfig2W(IntPtr service, uint infoLevel, ref int delayedAutostart);

    [DllImport("advapi32.dll", SetLastError = true)]
    public static extern bool CloseServiceHandle(IntPtr handle);

    // === Registry ===

    public const int KEY_READ = 0x20019;
    public const int KEY_ALL_ACCESS = 0xF003F;

    // === Privileges ===

    public const uint TOKEN_ADJUST_PRIVILEGES = 0x0020;
    public const uint TOKEN_QUERY = 0x0008;
    public const uint SE_PRIVILEGE_ENABLED = 0x0002;

    [StructLayout(LayoutKind.Sequential)]
    public struct LUID { public uint LowPart; public int HighPart; }

    [StructLayout(LayoutKind.Sequential)]
    public struct TOKEN_PRIVILEGES { public uint PrivilegeCount; public LUID Luid; public uint Attributes; }

    [DllImport("advapi32.dll", SetLastError = true)]
    public static extern bool OpenProcessToken(IntPtr process, uint desiredAccess, out SafeAccessTokenHandle token);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern bool LookupPrivilegeValueW(string? systemName, string name, out LUID luid);

    [DllImport("advapi32.dll", SetLastError = true)]
    public static extern bool AdjustTokenPrivileges(SafeAccessTokenHandle token, bool disableAll,
        ref TOKEN_PRIVILEGES newState, uint bufferLength, IntPtr previousState, IntPtr returnLength);

    [DllImport("kernel32.dll")]
    public static extern IntPtr GetCurrentProcess();

    /// <summary>Loads a hive file into a private namespace of this process; unloaded when the handle closes.
    /// No privileges and no global mount name, unlike RegLoadKey / reg.exe load.</summary>
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode)]
    public static extern int RegLoadAppKeyW(string file, out SafeRegistryHandle key, int samDesired, int options, int reserved);

    // === Local accounts ===

    public const uint UF_ACCOUNTDISABLE = 0x0002;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct USER_INFO_1
    {
        public string usri1_name;
        public string usri1_password;
        public uint usri1_password_age;
        public uint usri1_priv;
        public string usri1_home_dir;
        public string usri1_comment;
        public uint usri1_flags;
        public string usri1_script_path;
    }

    [DllImport("netapi32.dll", CharSet = CharSet.Unicode)]
    public static extern int NetUserGetInfo(string? serverName, string userName, int level, out IntPtr buffer);

    [DllImport("netapi32.dll")]
    public static extern int NetApiBufferFree(IntPtr buffer);

    // === Console ===

    [DllImport("kernel32.dll")]
    public static extern uint GetConsoleProcessList(uint[] processList, uint count);
}
