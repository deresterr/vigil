using System.Net.NetworkInformation;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;

namespace Vigil.Watchdog.Server;

/// <summary>
/// A run of failed calls to the server, logged once when it has lasted <see cref="ReportAfter"/> and once when it
/// ends (a repeating condition is logged at its start and its end). A single failure stays out of the Event Log:
/// Wi-Fi often joins seconds after logon, and a warning per failed heartbeat filled the log while the server was
/// down (one a second while locked) (#128). Program.cs lets this category's Information into the Event Log.
/// </summary>
public sealed class OutageLog
{
    public static readonly TimeSpan ReportAfter = TimeSpan.FromSeconds(30);

    private readonly string _what;
    private readonly ILogger _logger;
    private readonly object _gate = new();
    private long? _sinceMs;  // ServerClock.MonotonicMs of the first failure of the run
    private int _attempts;
    private bool _reported;

    /// <param name="what">What fails, for the log ("Heartbeat", "Guardian report").</param>
    public OutageLog(string what, ILogger<OutageLog> logger)
    {
        _what = what;
        _logger = logger;
    }

    public void Failure(Exception ex)
    {
        lock (_gate)
        {
            _sinceMs ??= ServerClock.MonotonicMs;
            _attempts++;
            var seconds = (ServerClock.MonotonicMs - _sinceMs.Value) / 1000;
            if (_reported || seconds < (long)ReportAfter.TotalSeconds) return;
            _reported = true;
            _logger.LogWarning(ex, "{What}: no answer from the server for {Seconds} s ({Cause}, {Attempts} attempts)",
                _what, seconds, Cause(ex), _attempts);
        }
    }

    public void Success()
    {
        lock (_gate)
        {
            if (_reported)
                _logger.LogInformation("{What}: the server answers again after {Seconds} s ({Attempts} failed attempts)",
                    _what, (ServerClock.MonotonicMs - _sinceMs!.Value) / 1000, _attempts);
            _sinceMs = null;
            _attempts = 0;
            _reported = false;
        }
    }

    private static string Cause(Exception ex) =>
        ex is KeyRejectedException ? "this computer's key was rejected"
        : IsNoNetwork(ex) ? "this PC has no network"
        : "server unreachable";

    /// <summary>
    /// True when the call failed because this PC has no network at all (Wi-Fi not joined yet, cable out, airplane
    /// mode): the connect found no route, or the name could not be resolved with no interface up. Anything else —
    /// host down, port closed, timeout, rejected key — is the server's side. Only the lock message depends on it,
    /// never the lock: the child can switch the network off without administrator rights.
    /// </summary>
    public static bool IsNoNetwork(Exception ex)
    {
        for (Exception? e = ex; e is not null; e = e.InnerException)
        {
            if (e is not SocketException se) continue;
            return se.SocketErrorCode switch
            {
                SocketError.NetworkUnreachable or SocketError.NetworkDown => true,
                SocketError.HostNotFound or SocketError.TryAgain or SocketError.NoData
                    => !NetworkInterface.GetIsNetworkAvailable(),
                _ => false,
            };
        }
        return false;
    }
}
