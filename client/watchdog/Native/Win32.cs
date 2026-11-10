using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Vigil.Watchdog.Native;

/// <summary>
/// Windows API P/Invoke, in one file so it is easy to audit. LibraryImport (source-generated marshalling)
/// where possible, classic DllImport for signatures with string fields in structs (SYSLIB1051).
/// </summary>
[SupportedOSPlatform("windows")]
public static partial class Win32
{
    // === Constants ===

    public const int WTS_CURRENT_SERVER_HANDLE = 0;

    // Token rights / types
    public const uint TOKEN_DUPLICATE = 0x0002;
    public const uint TOKEN_QUERY = 0x0008;
    public const uint TOKEN_ASSIGN_PRIMARY = 0x0001;
    public const uint TOKEN_ADJUST_DEFAULT = 0x0080;
    public const uint TOKEN_ALL_ACCESS_FOR_PROCESS_AS_USER =
        TOKEN_ASSIGN_PRIMARY | TOKEN_DUPLICATE | TOKEN_QUERY | TOKEN_ADJUST_DEFAULT;

    // SECURITY_IMPERSONATION_LEVEL
    public const int SecurityImpersonation = 2;
    // TOKEN_TYPE
    public const int TokenPrimary = 1;

    // CreateProcess flags
    public const uint NORMAL_PRIORITY_CLASS = 0x00000020;
    public const uint CREATE_UNICODE_ENVIRONMENT = 0x00000400;

    public enum WTS_CONNECTSTATE_CLASS
    {
        WTSActive = 0,         // user logged on, active session (also behind Win+L)
        WTSConnected = 1,      // connected, nobody signed in yet: the console at the logon screen
        WTSConnectQuery = 2,
        WTSShadow = 3,
        WTSDisconnected = 4,   // user logged on, session disconnected (e.g. fast user switching)
        WTSIdle = 5,
        WTSListen = 6,
        WTSReset = 7,
        WTSDown = 8,
        WTSInit = 9,
    }

    // === Structures ===

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct WTS_SESSION_INFO
    {
        public uint SessionId;
        [MarshalAs(UnmanagedType.LPWStr)]
        public string pWinStationName;
        public WTS_CONNECTSTATE_CLASS State;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct STARTUPINFO
    {
        public int cb;
        public string? lpReserved;
        public string? lpDesktop;
        public string? lpTitle;
        public uint dwX;
        public uint dwY;
        public uint dwXSize;
        public uint dwYSize;
        public uint dwXCountChars;
        public uint dwYCountChars;
        public uint dwFillAttribute;
        public uint dwFlags;
        public ushort wShowWindow;
        public ushort cbReserved2;
        public IntPtr lpReserved2;
        public IntPtr hStdInput;
        public IntPtr hStdOutput;
        public IntPtr hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct PROCESS_INFORMATION
    {
        public IntPtr hProcess;
        public IntPtr hThread;
        public uint dwProcessId;
        public uint dwThreadId;
    }

    // === wtsapi32.dll ===

    // WTSEnumerateSessions exists only as WTSEnumerateSessionsW / *A: without an explicit EntryPoint
    // LibraryImport throws EntryPointNotFoundException at start.
    [LibraryImport("wtsapi32.dll", EntryPoint = "WTSEnumerateSessionsW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool WTSEnumerateSessions(
        IntPtr hServer,
        uint Reserved,
        uint Version,
        out IntPtr ppSessionInfo,
        out uint pCount);

    [LibraryImport("wtsapi32.dll", SetLastError = true)]
    public static partial void WTSFreeMemory(IntPtr pMemory);

    [LibraryImport("wtsapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool WTSQueryUserToken(uint sessionId, out IntPtr Token);

    // === advapi32.dll ===

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool DuplicateTokenEx(
        IntPtr hExistingToken,
        uint dwDesiredAccess,
        IntPtr lpTokenAttributes,
        int ImpersonationLevel,
        int TokenType,
        out IntPtr phNewToken);

    // CreateProcessAsUser: classic DllImport, STARTUPINFO has string fields the source generator
    // cannot marshal (SYSLIB1051).
    [DllImport("advapi32.dll", SetLastError = true, EntryPoint = "CreateProcessAsUserW",
        CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool CreateProcessAsUser(
        IntPtr hToken,
        string? lpApplicationName,
        string? lpCommandLine,
        IntPtr lpProcessAttributes,
        IntPtr lpThreadAttributes,
        [MarshalAs(UnmanagedType.Bool)] bool bInheritHandles,
        uint dwCreationFlags,
        IntPtr lpEnvironment,
        string? lpCurrentDirectory,
        ref STARTUPINFO lpStartupInfo,
        out PROCESS_INFORMATION lpProcessInformation);

    // === userenv.dll ===

    /// <summary>The profile folder of the token's user, from ProfileList (not from the user's environment).</summary>
    [LibraryImport("userenv.dll", EntryPoint = "GetUserProfileDirectoryW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static unsafe partial bool GetUserProfileDirectory(IntPtr hToken, char* lpProfileDir, ref uint lpcchSize);

    // === kernel32.dll ===

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool CloseHandle(IntPtr hObject);

    // === Awake clock (tamper telemetry, Detection/AwakeClock.cs) ===
    // 100 ns units since boot, excluding time spent in sleep/hibernate.
    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool QueryUnbiasedInterruptTime(out ulong unbiasedTime);

    // === Security descriptors (vigil_ui process/thread DACL, Ui/UiProcessLauncher.cs) ===

    [StructLayout(LayoutKind.Sequential)]
    public struct SECURITY_ATTRIBUTES
    {
        public int nLength;
        public IntPtr lpSecurityDescriptor;
        public int bInheritHandle;
    }

    public const uint SDDL_REVISION_1 = 1;

    [LibraryImport("advapi32.dll", EntryPoint = "ConvertStringSecurityDescriptorToSecurityDescriptorW",
        SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool ConvertStringSecurityDescriptorToSecurityDescriptor(
        string stringSecurityDescriptor, uint revision, out IntPtr securityDescriptor, IntPtr securityDescriptorSize);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    public static partial IntPtr LocalFree(IntPtr hMem);

    // === Session logoff (kill-loop response, Ui/UiManager.cs) ===
    [LibraryImport("wtsapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool WTSLogoffSession(IntPtr hServer, uint sessionId, [MarshalAs(UnmanagedType.Bool)] bool bWait);
}
