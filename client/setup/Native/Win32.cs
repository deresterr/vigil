using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Vigil.Setup.Native;

/// <summary>P/Invoke for vigil_setup, in one file for auditing.</summary>
internal static class Win32
{
    // === Service control manager ===

    public const uint SC_MANAGER_CONNECT = 0x0001;
    public const uint READ_CONTROL = 0x00020000;
    public const uint DACL_SECURITY_INFORMATION = 0x00000004;

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern IntPtr OpenSCManagerW(string? machineName, string? databaseName, uint desiredAccess);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern IntPtr OpenServiceW(IntPtr scManager, string serviceName, uint desiredAccess);

    [DllImport("advapi32.dll", SetLastError = true)]
    public static extern bool QueryServiceObjectSecurity(IntPtr service, uint securityInformation,
        byte[]? securityDescriptor, uint bufferSize, out uint bytesNeeded);

    [DllImport("advapi32.dll", SetLastError = true)]
    public static extern bool CloseServiceHandle(IntPtr handle);

    // === Registry ===

    public const int KEY_READ = 0x20019;

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
