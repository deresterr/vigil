using System.Runtime.Versioning;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Hosting.WindowsServices;
using Microsoft.Extensions.Logging;
using Vigil.Watchdog;
using Vigil.Watchdog.Configuration;
using Vigil.Watchdog.Detection;
using Vigil.Watchdog.Guardian;
using Vigil.Watchdog.Ui;
using Vigil.Watchdog.Offline;
using Vigil.Watchdog.Server;
using Vigil.Shared;

[assembly: SupportedOSPlatform("windows")]

var configPath = VigilLayout.PathOf(VigilLayout.ConfigFile);
var signingPath = VigilLayout.PathOf(VigilLayout.SigningFile);

using var bootstrapLoggerFactory = LoggerFactory.Create(b => b.AddConsole().AddEventLog(eventLog =>
{
    eventLog.SourceName = VigilLayout.WatchdogEventSource;
    eventLog.LogName = "Application";
}));
var bootstrapLogger = bootstrapLoggerFactory.CreateLogger("Bootstrap");

VigilConfig vigilConfig;
byte[] signingKey;
try
{
    vigilConfig = VigilConfig.LoadFromFile(configPath);
    bootstrapLogger.LogInformation("Loaded vigil.toml: server={Server}", vigilConfig.ServerUrl);
    signingKey = DpapiKeyStore.Load(signingPath);
    bootstrapLogger.LogInformation("Signing key loaded ({Bytes} B)", signingKey.Length);
}
catch (Exception ex)
{
    bootstrapLogger.LogCritical(ex, "Startup failed: Vigil will not run");
    throw;
}

var builder = Host.CreateApplicationBuilder(args);

builder.Logging.ClearProviders();
builder.Logging.AddConsole();
builder.Logging.AddEventLog(eventLog =>
{
    eventLog.SourceName = VigilLayout.WatchdogEventSource;
    eventLog.LogName = "Application";
});

builder.Services.AddWindowsService(options =>
{
    options.ServiceName = VigilLayout.ServiceName;
});
// Replaces the default lifetime registered above (last registration wins).
if (WindowsServiceHelpers.IsWindowsService())
    builder.Services.AddSingleton<IHostLifetime, VigilServiceLifetime>();

builder.Services.AddSingleton(vigilConfig);
builder.Services.AddSingleton(sp => new TotpSecretStore(
    VigilLayout.PathOf(VigilLayout.TotpFile), sp.GetRequiredService<ILogger<TotpSecretStore>>()));
builder.Services.AddSingleton<OfflineUnlock>();
builder.Services.AddSingleton<ServerClock>();
builder.Services.AddSingleton(sp => new HeartbeatClient(
    vigilConfig, signingKey, sp.GetRequiredService<ServerClock>(), sp.GetRequiredService<ILogger<HeartbeatClient>>()));
builder.Services.AddSingleton<SessionDetector>();

builder.Services.AddSingleton(new UiManagerOptions { UiExePath = VigilLayout.PathOf(VigilLayout.UiExe) });
builder.Services.AddSingleton<NamedPipeIpcServer>();
builder.Services.AddSingleton<UiProcessLauncher>();
builder.Services.AddSingleton<IUiManager, UiManager>();

builder.Services.AddHostedService<Worker>();
builder.Services.AddHostedService<GuardianReporter>();

var host = builder.Build();
await host.RunAsync();
