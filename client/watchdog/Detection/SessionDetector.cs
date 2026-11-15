using System.Collections.Concurrent;
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
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class SessionDetector
{
    private readonly ILogger<SessionDetector> _logger;
    private readonly string? _childSid;
    // DOMAIN\user → SID: the lookup goes to LSA, the session's name is cheap to read.
    private readonly ConcurrentDictionary<string, string?> _sids = new(StringComparer.OrdinalIgnoreCase);
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
        if (sid is not null && sid.Equals(_childSid, StringComparison.OrdinalIgnoreCase))
        {
            _lastOther = null;
            return (true, session);
        }
        // Someone else's session (the parent's): not the child's time, no lock. Logged once per account.
        if (sid != _lastOther)
        {
            _lastOther = sid;
            _logger.LogInformation("Active session {Session} belongs to another account ({Sid}): not counted", session, sid ?? "?");
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

    private string? UserSidOf(uint sessionId)
    {
        var user = SessionString(sessionId, Win32.WTSUserName);
        if (string.IsNullOrEmpty(user)) return null;
        var domain = SessionString(sessionId, Win32.WTSDomainName) ?? "";
        return _sids.GetOrAdd($"{domain}\\{user}", _ =>
        {
            try { return ((SecurityIdentifier)new NTAccount(domain, user).Translate(typeof(SecurityIdentifier))).Value; }
            catch (SystemException ex)
            {
                _logger.LogWarning(ex, "No SID for {Domain}\\{User}", domain, user);
                return null;
            }
        });
    }

    private static string? SessionString(uint sessionId, int infoClass)
    {
        if (!Win32.WTSQuerySessionInformation(new IntPtr(Win32.WTS_CURRENT_SERVER_HANDLE), sessionId, infoClass,
                out var buffer, out _))
            return null;
        try { return Marshal.PtrToStringUni(buffer); }
        finally { Win32.WTSFreeMemory(buffer); }
    }
}
