using System.Runtime.Versioning;
using Vigil.Shared;
using Vigil.Watchdog.Configuration;
using Vigil.Watchdog.Server;

namespace Vigil.Watchdog.Detection;

/// <summary>
/// Whether the child's account is an administrator, for the heartbeat (#165): the panel shows it and journals a
/// change from no to yes. The account is the one in vigil.toml (standard-account setup) or, in the one-account
/// setup, the one in the active session. Detection only: an administrator can make the PC report anything.
/// Read at most once a minute; the last answer stays while nobody is logged on.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class ChildAdminCheck(VigilConfig config, SessionDetector sessions)
{
    private const long IntervalMs = 60_000;
    private bool? _last;
    private long? _checkedMs;

    public bool? Current()
    {
        var now = ServerClock.MonotonicMs;
        if (_checkedMs is { } at && now - at < IntervalMs) return _last;
        var sid = config.ChildSid ?? sessions.ActiveSessionUserSid();
        if (sid is null) return _last;
        _checkedMs = now;
        _last = LocalAdmins.Contains(sid) ?? _last;
        return _last;
    }
}
