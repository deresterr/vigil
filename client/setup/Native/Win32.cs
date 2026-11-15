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

    public static readonly IntPtr HKEY_USERS = unchecked((IntPtr)(int)0x80000003);

    /// <summary>Mounts a hive file under HKEY_USERS\&lt;subKey&gt; (SeBackup + SeRestore), replaying its .LOG files
    /// like a sign-in does. Not RegLoadAppKey: an app hive does not replay them and refuses the hive a sign-out left
    /// dirty with ERROR_BADDB (1009).</summary>
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode)]
    public static extern int RegLoadKeyW(IntPtr key, string subKey, string file);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode)]
    public static extern int RegUnLoadKeyW(IntPtr key, string subKey);

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

    // The child's own account (#160)
    public const uint USER_PRIV_USER = 1;
    public const uint UF_SCRIPT = 0x0001;
    public const uint UF_PASSWD_NOTREQD = 0x0020;
    public const uint UF_DONT_EXPIRE_PASSWD = 0x10000;
    public const int ERROR_MEMBER_IN_ALIAS = 1378;
    public const int ERROR_LOGON_FAILURE = 1326;
    public const int ERROR_ACCOUNT_RESTRICTION = 1327;
    public const int LOGON32_LOGON_INTERACTIVE = 2;
    public const int LOGON32_PROVIDER_DEFAULT = 0;
    public const int HRESULT_ALREADY_EXISTS = unchecked((int)0x800700B7);

    [DllImport("netapi32.dll", CharSet = CharSet.Unicode)]
    public static extern int NetUserAdd(string? serverName, int level, ref USER_INFO_1 buffer, out int parmError);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct LOCALGROUP_MEMBERS_INFO_3 { public string lgrmi3_domainandname; }

    [DllImport("netapi32.dll", CharSet = CharSet.Unicode)]
    public static extern int NetLocalGroupAddMembers(string? serverName, string groupName, int level,
        ref LOCALGROUP_MEMBERS_INFO_3 buffer, int totalEntries);

    /// <summary>A change as the user (old password known), not an administrator's reset: DPAPI keeps the
    /// account's protected data (saved browser passwords, Credential Manager).</summary>
    [DllImport("netapi32.dll", CharSet = CharSet.Unicode)]
    public static extern int NetUserChangePassword(string? domainName, string? userName, string oldPassword, string newPassword);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern bool LogonUserW(string userName, string? domain, string password, int logonType,
        int logonProvider, out SafeAccessTokenHandle token);

    /// <summary>The profile (NTUSER.DAT from the Default profile) without a logon.</summary>
    [DllImport("userenv.dll", CharSet = CharSet.Unicode)]
    public static extern int CreateProfile(string sid, string userName, [Out] char[] profilePath, uint size);

    // === Console ===

    [DllImport("kernel32.dll")]
    public static extern uint GetConsoleProcessList(uint[] processList, uint count);
}
