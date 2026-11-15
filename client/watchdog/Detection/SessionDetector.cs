using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Principal;
using Vigil.Watchdog.Configuration;
using Vigil.Watchdog.Native;

namespace Vigil.Watchdog.Detection;

/// <summary>
/// The child's session, if one is in Active state. Not Connected: that is the console at the logon screen with
/// nobody signed in — counting it charged the child for a PC left at the logon screen.
///
/// Whose session: with a child's account in vigil.toml (the standard-account setup, D30) only its sessions — the
/// parent's own account is neither counted nor locked. Without (the one-account setup, installs up to 0.9) any
/// active session is the child's: there an account a child-administrator creates is enforced too.
///
/// The account comes from the session's token (WTSQueryUserToken, SYSTEM only), not its user name: a name can be
/// renamed or reused, a SID cannot. A session whose account cannot be told is the child's (fail-closed): at worst
/// the parent is counted and locked, never the child left free.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class SessionDetector
{
    private readonly ILogger<SessionDetector> _logger;
    private readonly string? _childSid;
    private string? _lastOther;

    public SessionDetector(VigilConfig config, ILogger<SessionDetector> logger)
    {
        _childSid = config.ChildSid;
        _logger = logger;
    }

    /// <summary>
    /// (Active, SessionId): Active when the child's session is WTSActive; SessionId null otherwise.
    /// </summary>
    public (bool Active, uint? SessionId) DetectActiveSession()
    {
        var session = ActiveSession();
        if (session is null) return (false, null);
        if (_childSid is null) return (true, session);

        var sid = UserSidOf(session.Value);
        if (sid is null || sid.Equals(_childSid, StringComparison.OrdinalIgnoreCase))
        {
            _lastOther = null;
            return (true, session);
        }
        // Someone else's session (the parent's): not the child's time, no lock. Logged once per account.
        if (sid != _lastOther)
        {
            _lastOther = sid;
            _logger.LogInformation("Active session {Session} belongs to another account ({Sid}): not counted", session, sid);
        }
        return (false, null);
    }

    /// <summary>The SID of the account in the active session, whoever it is; null when nobody is logged on.</summary>
    public string? ActiveSessionUserSid() => ActiveSession() is { } s ? UserSidOf(s) : null;

    private uint? ActiveSession()
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
                return null;
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
                    return info.SessionId;
                }
            }

            return null;
        }
        finally
        {
            if (sessionInfoPtr != IntPtr.Zero)
            {
                Win32.WTSFreeMemory(sessionInfoPtr);
            }
        }
    }

    /// <summary>The SID of the session's user, from its token; null when it cannot be read (logged).</summary>
    private string? UserSidOf(uint sessionId)
    {
        if (!Win32.WTSQueryUserToken(sessionId, out var token))
        {
            _logger.LogWarning("WTSQueryUserToken failed for session {Session}, error {Error}: counted as the child's",
                sessionId, Marshal.GetLastWin32Error());
            return null;
        }
        try
        {
            using var identity = new WindowsIdentity(token);
            return identity.User?.Value;
        }
        catch (SystemException ex)
        {
            _logger.LogWarning(ex, "No SID for session {Session}: counted as the child's", sessionId);
            return null;
        }
        finally { Win32.CloseHandle(token); }
    }
}
