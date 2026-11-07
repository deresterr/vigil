using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
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
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class UiProcessLauncher : IDisposable
{
    public const string ProcessName = "vigil_ui";
    // Processes of earlier versions (separate lockscreen + tray, before D8) killed as orphans too.
    private static readonly string[] OrphanNames = { ProcessName, "vigil_lockscreen", "vigil_tray" };

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
                dwCreationFlags: Win32.NORMAL_PRIORITY_CLASS,
                lpEnvironment: IntPtr.Zero,
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

    /// <summary>
    /// Kills UI processes we do not track in the target session: survivors of a previous watchdog
    /// instance, or the separate lockscreen/tray of an earlier version still running after an upgrade.
    /// </summary>
    private void KillOrphans(uint sessionId)
    {
        foreach (var name in OrphanNames)
        {
            Process[] candidates;
            try { candidates = Process.GetProcessesByName(name); }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "GetProcessesByName({Name}) failed", name);
                continue;
            }
            foreach (var p in candidates)
            {
                try
                {
                    if ((uint)p.SessionId != sessionId) continue;
                    _logger.LogInformation("Killing orphan {Name} pid={Pid}", name, p.Id);
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
