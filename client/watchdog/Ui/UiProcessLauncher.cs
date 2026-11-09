using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Microsoft.Extensions.Logging;
using Vigil.Watchdog.Native;

namespace Vigil.Watchdog.Ui;

/// <summary>
/// Starts vigil_ui.exe in the child's session with the session user's token.
///
/// The watchdog runs as SYSTEM in session 0, so Process.Start would put the UI on an invisible
/// window station. Instead: WTSQueryUserToken(session) → DuplicateTokenEx(primary) →
/// CreateProcessAsUser on WinSta0\Default.
///
/// Process and main-thread DACL (friction): the user may query and wait on the process, but has no
/// PROCESS_TERMINATE / SUSPEND / WRITE_DAC. OWNER RIGHTS (OW) caps the owner's implicit rights, so
/// the child cannot just rewrite the DACL either. Taskkill, Stop-Process and Task Manager from a
/// non-elevated session get "access denied". An elevated admin is in BA (and has SeDebugPrivilege)
/// and can still kill it; the kill loop then logs the session off (UiManager).
///
/// Environment (D24): built here from a fixed list, nothing taken from the child's own environment. The
/// child may set user variables (HKCU\Environment) without elevation, and the .NET runtime reads several of
/// them (startup hooks, profilers) — a way to run code inside the lock screen that the process DACL closes
/// otherwise. System values come from the service's environment (machine-wide, admin-only), the profile
/// folders from the token's profile (ProfileList), and diagnostics are off: no debugger, profiler or
/// EventPipe IPC channel a same-user process could attach through. Friction, like the DACL.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class UiProcessLauncher : IDisposable
{
    public const string ProcessName = "vigil_ui";
    /// <summary>Copied from the service's (SYSTEM) environment: machine-wide values only admins can set.</summary>
    private static readonly string[] SystemVariables =
    {
        "SystemRoot", "windir", "SystemDrive", "ComSpec", "PATH", "PATHEXT", "ProgramData", "ALLUSERSPROFILE",
        "PUBLIC", "ProgramFiles", "ProgramFiles(x86)", "ProgramW6432", "CommonProgramFiles",
        "CommonProgramFiles(x86)", "CommonProgramW6432", "NUMBER_OF_PROCESSORS", "OS", "PROCESSOR_ARCHITECTURE",
        "PROCESSOR_IDENTIFIER", "PROCESSOR_LEVEL", "PROCESSOR_REVISION",
    };

    // 0x121410 = READ_CONTROL | SYNCHRONIZE | PROCESS_QUERY_INFORMATION | PROCESS_QUERY_LIMITED_INFORMATION | PROCESS_VM_READ
    private const string ProcessSddl = "D:(A;;GA;;;SY)(A;;GA;;;BA)(A;;0x121410;;;AU)(A;;0x121410;;;OW)";
    // 0x120800 = READ_CONTROL | SYNCHRONIZE | THREAD_QUERY_LIMITED_INFORMATION
    private const string ThreadSddl = "D:(A;;GA;;;SY)(A;;GA;;;BA)(A;;0x120800;;;AU)(A;;0x120800;;;OW)";

    private readonly ILogger<UiProcessLauncher> _logger;
    private IntPtr _processHandle = IntPtr.Zero;
    private uint _processId;
    private bool _disposed;

    public UiProcessLauncher(ILogger<UiProcessLauncher> logger)
    {
        _logger = logger;
    }

    public bool IsRunning
    {
        get
        {
            if (_processHandle == IntPtr.Zero) return false;
            try
            {
                using var p = Process.GetProcessById((int)_processId);
                return !p.HasExited;
            }
            catch (ArgumentException)
            {
                return false;
            }
        }
    }

    /// <summary>Starts vigil_ui.exe in <paramref name="sessionId"/>; returns its PID.</summary>
    public uint Launch(string exePath, uint sessionId)
    {
        if (!File.Exists(exePath))
            throw new FileNotFoundException($"{exePath} does not exist", exePath);

        Kill();
        KillOrphans(sessionId);

        if (!Win32.WTSQueryUserToken(sessionId, out var impersonationToken))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(),
                $"WTSQueryUserToken failed for session {sessionId} (nobody logged on?)");
        }

        IntPtr primaryToken = IntPtr.Zero;
        IntPtr processSa = IntPtr.Zero, threadSa = IntPtr.Zero;
        IntPtr environment = IntPtr.Zero;
        try
        {
            if (!Win32.DuplicateTokenEx(
                    impersonationToken,
                    Win32.TOKEN_ALL_ACCESS_FOR_PROCESS_AS_USER,
                    IntPtr.Zero,
                    Win32.SecurityImpersonation,
                    Win32.TokenPrimary,
                    out primaryToken))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "DuplicateTokenEx failed");
            }

            environment = Marshal.StringToHGlobalUni(EnvironmentBlock(ProfileDirectory(primaryToken)));

            processSa = AllocSecurityAttributes(ProcessSddl);
            threadSa = AllocSecurityAttributes(ThreadSddl);

            var si = new Win32.STARTUPINFO
            {
                cb = Marshal.SizeOf<Win32.STARTUPINFO>(),
                lpDesktop = @"WinSta0\Default",
            };

            var ok = Win32.CreateProcessAsUser(
                primaryToken,
                exePath,
                null,
                processSa, threadSa,
                bInheritHandles: false,
                dwCreationFlags: Win32.NORMAL_PRIORITY_CLASS | Win32.CREATE_UNICODE_ENVIRONMENT,
                lpEnvironment: environment,
                lpCurrentDirectory: Path.GetDirectoryName(exePath),
                lpStartupInfo: ref si,
                out var pi);
            if (!ok)
                throw new Win32Exception(Marshal.GetLastWin32Error(), $"CreateProcessAsUser failed for {exePath}");

            Win32.CloseHandle(pi.hThread);
            _processHandle = pi.hProcess;
            _processId = pi.dwProcessId;
            _logger.LogInformation("Spawned vigil_ui pid={Pid} session={Session}", pi.dwProcessId, sessionId);
            return pi.dwProcessId;
        }
        finally
        {
            if (environment != IntPtr.Zero) Marshal.FreeHGlobal(environment);
            FreeSecurityAttributes(processSa);
            FreeSecurityAttributes(threadSa);
            if (impersonationToken != IntPtr.Zero) Win32.CloseHandle(impersonationToken);
            if (primaryToken != IntPtr.Zero) Win32.CloseHandle(primaryToken);
        }
    }

    /// <summary>Ends the process we started, if any. Idempotent.</summary>
    public void Kill()
    {
        if (_processHandle == IntPtr.Zero) return;
        try
        {
            using var p = Process.GetProcessById((int)_processId);
            if (!p.HasExited)
            {
                p.Kill();
                p.WaitForExit(2000);
            }
        }
        catch (ArgumentException) { /* already gone */ }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not kill vigil_ui pid={Pid}", _processId);
        }
        finally
        {
            Win32.CloseHandle(_processHandle);
            _processHandle = IntPtr.Zero;
            _processId = 0;
        }
    }

    /// <summary>Kills vigil_ui processes we do not track in the target session (survivors of a previous
    /// watchdog instance).</summary>
    private void KillOrphans(uint sessionId)
    {
        Process[] candidates;
        try { candidates = Process.GetProcessesByName(ProcessName); }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "GetProcessesByName({Name}) failed", ProcessName);
            return;
        }
        foreach (var p in candidates)
        {
            try
            {
                if ((uint)p.SessionId != sessionId) continue;
                _logger.LogInformation("Killing orphan {Name} pid={Pid}", ProcessName, p.Id);
                p.Kill();
                p.WaitForExit(2000);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Could not kill orphan pid={Pid}", p.Id);
            }
            finally
            {
                p.Dispose();
            }
        }
    }

    /// <summary>The user's profile folder from the token (ProfileList), or null.</summary>
    private unsafe string? ProfileDirectory(IntPtr token)
    {
        uint size = 0;
        Win32.GetUserProfileDirectory(token, null, ref size);
        if (size == 0) return null;
        var buffer = new char[size];
        fixed (char* p = buffer)
        {
            if (!Win32.GetUserProfileDirectory(token, p, ref size))
            {
                _logger.LogWarning(new Win32Exception(Marshal.GetLastWin32Error()), "GetUserProfileDirectory failed");
                return null;
            }
        }
        return new string(buffer, 0, (int)size - 1);
    }

    /// <summary>
    /// A Unicode environment block: NAME=value entries, each ending in NUL, sorted, one more NUL at the end.
    /// Without a profile folder vigil_ui still starts: only its log needs it.
    /// </summary>
    internal static string EnvironmentBlock(string? profile)
    {
        var vars = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in SystemVariables)
            if (Environment.GetEnvironmentVariable(name) is { Length: > 0 } value) vars[name] = value;
        if (profile is not null)
        {
            var local = Path.Combine(profile, "AppData", "Local");
            vars["USERPROFILE"] = profile;
            vars["APPDATA"] = Path.Combine(profile, "AppData", "Roaming");
            vars["LOCALAPPDATA"] = local;
            vars["TEMP"] = vars["TMP"] = Path.Combine(local, "Temp");
        }
        // Debugger, profiler and EventPipe off: the runtime opens no diagnostic IPC channel.
        vars["DOTNET_EnableDiagnostics"] = "0";

        var block = new StringBuilder();
        foreach (var (name, value) in vars) block.Append(name).Append('=').Append(value).Append('\0');
        return block.Append('\0').ToString();
    }

    private static IntPtr AllocSecurityAttributes(string sddl)
    {
        if (!Win32.ConvertStringSecurityDescriptorToSecurityDescriptor(sddl, Win32.SDDL_REVISION_1, out var sd, IntPtr.Zero))
            throw new Win32Exception(Marshal.GetLastWin32Error(), $"Invalid SDDL {sddl}");
        var sa = new Win32.SECURITY_ATTRIBUTES
        {
            nLength = Marshal.SizeOf<Win32.SECURITY_ATTRIBUTES>(),
            lpSecurityDescriptor = sd,
            bInheritHandle = 0,
        };
        var ptr = Marshal.AllocHGlobal(sa.nLength);
        Marshal.StructureToPtr(sa, ptr, false);
        return ptr;
    }

    private static void FreeSecurityAttributes(IntPtr ptr)
    {
        if (ptr == IntPtr.Zero) return;
        var sa = Marshal.PtrToStructure<Win32.SECURITY_ATTRIBUTES>(ptr);
        if (sa.lpSecurityDescriptor != IntPtr.Zero) Win32.LocalFree(sa.lpSecurityDescriptor);
        Marshal.FreeHGlobal(ptr);
    }

    public void Dispose()
    {
        if (_disposed) return;
        Kill();
        _disposed = true;
    }
}
