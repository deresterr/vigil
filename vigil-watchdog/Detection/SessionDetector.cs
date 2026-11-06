using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;
using Vigil.Watchdog.Native;

namespace Vigil.Watchdog.Detection;

/// <summary>
/// Wykrywa czy ktoś jest aktualnie zalogowany (sesja Active lub Connected).
///
/// Spec: tylko jedno konto na PC dziecka, więc dowolna aktywna sesja = sesja dziecka.
/// Nie filtrujemy po SID — watchdog jest service'em, ma dostęp do wszystkich sesji.
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
    /// Zwraca (sessionActive, sessionId). sessionId może być null jeśli nikt nie jest zalogowany.
    /// sessionActive=true gdy state == WTSActive lub WTSConnected.
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

                // SessionId 0 to "Services" session, ignorujemy
                if (info.SessionId == 0) continue;

                if (info.State == Win32.WTS_CONNECTSTATE_CLASS.WTSActive
                    || info.State == Win32.WTS_CONNECTSTATE_CLASS.WTSConnected)
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
