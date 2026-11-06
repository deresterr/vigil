using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;
using Vigil.Watchdog.Native;

namespace Vigil.Watchdog.Lockscreen;

/// <summary>
/// Spawnuje vigil_lockscreen.exe w user session używając tokenu sesji dziecka.
///
/// Dlaczego nie zwykły Process.Start: vigil_watchdog działa jako SYSTEM w session 0.
/// Process.Start uruchomiłby vigil_lockscreen w session 0 — okno nigdy by się nie wyświetliło
/// na ekranie dziecka (jest na innej stacji okiennej WinSta0).
///
/// Procedura:
/// 1. WTSQueryUserToken(sessionId) → impersonation token sesji dziecka
/// 2. DuplicateTokenEx(impersonation, primary) → primary token (CreateProcessAsUser wymaga primary)
/// 3. CreateProcessAsUser z lpDesktop="WinSta0\\Default" — okna pójdą na właściwą stację
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class LockscreenProcessLauncher : IDisposable
{
    private readonly ILogger<LockscreenProcessLauncher> _logger;
    private IntPtr _processHandle = IntPtr.Zero;
    private uint _processId;
    private bool _disposed;

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

    public LockscreenProcessLauncher(ILogger<LockscreenProcessLauncher> logger)
    {
        _logger = logger;
    }

    /// <summary>Uruchamia vigil_lockscreen.exe w user session sessionId na desktopName.</summary>
    /// <param name="additionalArgs">Opcjonalne argumenty CLI (np. "--restore-taskbar-only").</param>
    /// <param name="trackProcess">
    /// True (domyślnie) = śledź proces (IsRunning/Kill).
    /// False = fire-and-forget (nie nadpisuj _processHandle; nie wywołuj KillOrphans).
    /// </param>
    public void Launch(string lockscreenExePath, uint sessionId, string desktopName = "Default",
                       string? additionalArgs = null, bool trackProcess = true)
    {
        if (!File.Exists(lockscreenExePath))
        {
            throw new FileNotFoundException(
                $"vigil_lockscreen.exe nie istnieje pod {lockscreenExePath}", lockscreenExePath);
        }

        // Cleanup orphanów tylko gdy uruchamiamy główny lockscreen, nie helper.
        if (trackProcess)
            KillOrphans(sessionId);

        // 1. Token sesji dziecka (impersonation level)
        if (!Win32.WTSQueryUserToken(sessionId, out var impersonationToken))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(),
                $"WTSQueryUserToken failed dla sessionId={sessionId}. " +
                "Czy jakikolwiek user jest zalogowany?");
        }

        IntPtr primaryToken = IntPtr.Zero;
        try
        {
            // 2. Duplikuj na primary token
            if (!Win32.DuplicateTokenEx(
                    impersonationToken,
                    Win32.TOKEN_ALL_ACCESS_FOR_PROCESS_AS_USER,
                    IntPtr.Zero,
                    Win32.SecurityImpersonation,
                    Win32.TokenPrimary,
                    out primaryToken))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(),
                    "DuplicateTokenEx failed");
            }

            // 3. CreateProcessAsUser
            var si = new Win32.STARTUPINFO
            {
                cb = Marshal.SizeOf<Win32.STARTUPINFO>(),
                lpDesktop = $@"WinSta0\{desktopName}",
            };

            // Gdy podano additionalArgs, budujemy lpCommandLine z pełną ścieżką w cudzysłowie
            // (wymaganie Windows: argv[0] = ścieżka exe, jeśli lpApplicationName jest non-null
            //  to lpCommandLine jest traktowane jako surowe args — cytujemy dla bezpieczeństwa).
            string? commandLine = additionalArgs is not null
                ? $"\"{lockscreenExePath}\" {additionalArgs}"
                : null;

            var ok = Win32.CreateProcessAsUser(
                primaryToken,
                lockscreenExePath,
                commandLine,
                IntPtr.Zero, IntPtr.Zero,   // process/thread security attributes
                bInheritHandles: false,
                dwCreationFlags: Win32.NORMAL_PRIORITY_CLASS,
                lpEnvironment: IntPtr.Zero,
                lpCurrentDirectory: Path.GetDirectoryName(lockscreenExePath),
                lpStartupInfo: ref si,
                out var pi);

            if (!ok)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(),
                    $"CreateProcessAsUser failed dla {lockscreenExePath}");
            }

            Win32.CloseHandle(pi.hThread);

            if (trackProcess)
            {
                // Śledź proces: IsRunning/Kill będą używać tego handle.
                _processHandle = pi.hProcess;
                _processId = pi.dwProcessId;
                _logger.LogInformation(
                    "Spawned vigil_lockscreen pid={Pid} session={Session} desktop={Desktop}",
                    pi.dwProcessId, sessionId, desktopName);
            }
            else
            {
                // Fire-and-forget helper — nie nadpisuj stanu trackowanego procesu.
                Win32.CloseHandle(pi.hProcess);
                _logger.LogDebug(
                    "Launched helper vigil_lockscreen pid={Pid} args='{Args}' (fire-and-forget)",
                    pi.dwProcessId, additionalArgs);
            }
        }
        finally
        {
            if (impersonationToken != IntPtr.Zero) Win32.CloseHandle(impersonationToken);
            if (primaryToken != IntPtr.Zero) Win32.CloseHandle(primaryToken);
        }
    }

    /// <summary>Kończy proces lockscreen jeśli istnieje. Idempotent.</summary>
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
        catch (ArgumentException)
        {
            // Już nie istnieje
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Nie udało się zabić vigil_lockscreen pid={Pid}", _processId);
        }
        finally
        {
            Win32.CloseHandle(_processHandle);
            _processHandle = IntPtr.Zero;
            _processId = 0;
        }
    }

    /// <summary>
    /// Zabija orphaned vigil_lockscreen.exe (z poprzedniej instancji watchdoga które przeżyły crash).
    /// Sprawdza tylko procesy w docelowej sesji żeby nie ingerować w inne usera.
    /// </summary>
    private void KillOrphans(uint targetSessionId)
    {
        Process[] candidates;
        try
        {
            candidates = Process.GetProcessesByName("vigil_lockscreen");
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "GetProcessesByName failed");
            return;
        }

        foreach (var p in candidates)
        {
            try
            {
                // Pomijamy proces który sami trzymamy (po _launcher.Kill() _processId=0, więc 0 != match)
                if ((uint)p.Id == _processId) { p.Dispose(); continue; }
                if ((uint)p.SessionId != targetSessionId) { p.Dispose(); continue; }
                _logger.LogInformation("KillOrphans: zabijam orphan vigil_lockscreen pid={Pid} session={Session}",
                    p.Id, p.SessionId);
                p.Kill();
                p.WaitForExit(2000);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "KillOrphans: failed dla pid={Pid}", p.Id);
            }
            finally
            {
                p.Dispose();
            }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        Kill();
        _disposed = true;
    }
}
