﻿using System.Collections.Concurrent;
using System.Runtime.Versioning;
using System.ServiceProcess;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Hosting.WindowsServices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Vigil.Watchdog;

/// <summary>
/// WindowsServiceLifetime that remembers what the SCM told the service about the system:
/// - a system shutdown, so the goodbye sent to the server can tell "PC shutting down" from "service stopped";
/// - session logoffs, so UiManager does not take vigil_ui closing with its session for a kill. Windows logs the
///   session off (closing vigil_ui) before it stops services, and nothing in the session says so: the SCM's
///   SERVICE_CONTROL_SESSIONCHANGE does, and a process in the child's session cannot fake it.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class VigilServiceLifetime : WindowsServiceLifetime
{
    private static volatile bool _systemShutdown;
    private static readonly ConcurrentDictionary<int, DateTime> LogoffsUtc = new();
    private readonly ILogger _logger;

    public static bool IsSystemShutdown => _systemShutdown;

    /// <summary>True if the SCM reported a logoff of <paramref name="sessionId"/> at or after
    /// <paramref name="sinceUtc"/>, or the system is shutting down.</summary>
    public static bool SessionEndedSince(uint sessionId, DateTime sinceUtc) =>
        _systemShutdown || (LogoffsUtc.TryGetValue((int)sessionId, out var at) && at >= sinceUtc);

    public VigilServiceLifetime(
        IHostEnvironment environment,
        IHostApplicationLifetime applicationLifetime,
        ILoggerFactory loggerFactory,
        IOptions<HostOptions> optionsAccessor,
        IOptions<WindowsServiceLifetimeOptions> windowsServiceOptionsAccessor)
        : base(environment, applicationLifetime, loggerFactory, optionsAccessor, windowsServiceOptionsAccessor)
    {
        _logger = loggerFactory.CreateLogger<VigilServiceLifetime>();
        CanShutdown = true;
        CanHandleSessionChangeEvent = true;
    }

    protected override void OnShutdown()
    {
        _systemShutdown = true;
        base.OnShutdown();  // stops the host and waits for it (StopAsync -> goodbye)
    }

    protected override void OnSessionChange(SessionChangeDescription changeDescription)
    {
        if (changeDescription.Reason == SessionChangeReason.SessionLogoff)
            LogoffsUtc[changeDescription.SessionId] = DateTime.UtcNow;
        _logger.LogInformation("Session {Session}: {Reason}", changeDescription.SessionId, changeDescription.Reason);
        base.OnSessionChange(changeDescription);
    }
}
