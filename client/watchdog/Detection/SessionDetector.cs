using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;
using Vigil.Watchdog.Native;

namespace Vigil.Watchdog.Detection;

/// <summary>
/// Whether someone is logged on (a session in Active state). The child's account is the only interactive one
/// (D12), so any such session is the child's; no filtering by SID. Not Connected: that is the console at the
/// logon screen with nobody signed in — counting it charged the child for a PC left at the logon screen.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class SessionDetector
{
    private readonly ILogger<SessionDetector> _logger;

    public SessionDetector(ILogger<SessionDetector> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// (Active, SessionId): Active when a session is WTSActive; SessionId null when nobody is logged on.
    /// </summary>
    public (bool Active, uint? SessionId) DetectActiveSession()
    {
        IntPtr sessionInfoPtr = IntPtr.Zero;
        try
        {
            if (!Win32.WTSEnumerateSessions(
                    new IntPtr(Win32.WTS_CURRENT_SERVER_HANDLE), 0, 1,
                    out sessionInfoPtr, out var count))
            {
                _logger.LogWarning(
                    "WTSEnumerateSessions failed, error {Error}",
                    Marshal.GetLastWin32Error());
                return (false, null);
            }

            var size = Marshal.SizeOf<Win32.WTS_SESSION_INFO>();
            for (var i = 0; i < count; i++)
            {
                var element = (IntPtr)((long)sessionInfoPtr + i * size);
                var info = Marshal.PtrToStructure<Win32.WTS_SESSION_INFO>(element);

                // Session 0 is the services session: skipped
                if (info.SessionId == 0) continue;

                if (info.State == Win32.WTS_CONNECTSTATE_CLASS.WTSActive)
                {
                    return (true, info.SessionId);
                }
            }

            return (false, null);
        }
        finally
        {
            if (sessionInfoPtr != IntPtr.Zero)
            {
                Win32.WTSFreeMemory(sessionInfoPtr);
            }
        }
    }
}
