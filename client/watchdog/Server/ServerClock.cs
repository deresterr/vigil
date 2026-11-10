using Microsoft.Extensions.Logging;

namespace Vigil.Watchdog.Server;

/// <summary>
/// The watchdog never trusts the PC's wall clock (#92). An administrator can set it, and a fresh VM may run an
/// hour off; both broke things measured on it.
/// - <see cref="UtcNow"/>: real time, the PC clock corrected by the server's X-Server-Time (every /v1 response,
///   also the rejections). Where the time itself matters: the request timestamp the server checks (±60 s), the
///   offline TOTP check, the time on our own lock screen. Not current_time_utc in the heartbeat: that stays the
///   PC's own clock so the server can journal the drift (clock_drift) for the parent.
/// - <see cref="MonotonicMs"/>: for durations (grace period, offline grant and lockout, kill window). It counts
///   sleep (a PC woken up after an hour is an hour past its last heartbeat) and no clock change moves it.
/// The header is unsigned; a forged time only gets requests rejected, which keeps the PC locked (fail-closed).
/// </summary>
public sealed class ServerClock
{
    private readonly ILogger<ServerClock> _logger;
    private long _offsetMs;          // server minus PC
    private long _loggedOffsetMs;

    public ServerClock(ILogger<ServerClock> logger) => _logger = logger;

    public static long MonotonicMs => Environment.TickCount64;

    public DateTime UtcNow => DateTime.UtcNow.AddMilliseconds(Interlocked.Read(ref _offsetMs));

    public DateTime LocalNow => UtcNow.ToLocalTime();

    /// <summary>The server's time from a response that just arrived (Unix ms).</summary>
    public void Observe(long serverUnixMs)
    {
        var offset = serverUnixMs - DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        Interlocked.Exchange(ref _offsetMs, offset);
        // Logged when it moves by more than the round trip could explain: once for a wrong clock, once when
        // Windows sets it right.
        if (Math.Abs(offset - Interlocked.Read(ref _loggedOffsetMs)) > 30_000)
        {
            Interlocked.Exchange(ref _loggedOffsetMs, offset);
            _logger.LogWarning("PC clock differs from the server's by {Seconds} s (positive: ahead); using the server's time",
                -offset / 1000);
        }
    }
}
