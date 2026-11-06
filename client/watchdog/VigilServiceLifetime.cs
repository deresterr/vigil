using System.Runtime.Versioning;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Hosting.WindowsServices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Vigil.Watchdog;

/// <summary>
/// WindowsServiceLifetime that remembers whether the stop came from a system shutdown, so the
/// goodbye sent to the server can tell "PC shutting down" from "service stopped".
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class VigilServiceLifetime : WindowsServiceLifetime
{
    private static volatile bool _systemShutdown;

    public static bool IsSystemShutdown => _systemShutdown;

    public VigilServiceLifetime(
        IHostEnvironment environment,
        IHostApplicationLifetime applicationLifetime,
        ILoggerFactory loggerFactory,
        IOptions<HostOptions> optionsAccessor,
        IOptions<WindowsServiceLifetimeOptions> windowsServiceOptionsAccessor)
        : base(environment, applicationLifetime, loggerFactory, optionsAccessor, windowsServiceOptionsAccessor)
    {
        CanShutdown = true;
    }

    protected override void OnShutdown()
    {
        _systemShutdown = true;
        base.OnShutdown();  // stops the host and waits for it (StopAsync -> goodbye)
    }
}
