using System.Runtime.Versioning;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Hosting.WindowsServices;
using Microsoft.Extensions.Logging;
using Vigil.Watchdog;
using Vigil.Watchdog.Configuration;
using Vigil.Watchdog.Detection;
using Vigil.Watchdog.Lockscreen;
using Vigil.Watchdog.Offline;
using Vigil.Watchdog.Server;

[assembly: SupportedOSPlatform("windows")]

const string ProgramDataRoot = @"C:\ProgramData\Vigil";
const string TomlConfigFileName = "vigil.toml";
const string SigningDatName = "signing.dat";

var configPath = Path.Combine(ProgramDataRoot, TomlConfigFileName);
var signingPath = Path.Combine(ProgramDataRoot, SigningDatName);

using var bootstrapLoggerFactory = LoggerFactory.Create(b => b.AddConsole().AddEventLog(eventLog =>
{
    eventLog.SourceName = "Vigil Watchdog";
    eventLog.LogName = "Application";
}));
var bootstrapLogger = bootstrapLoggerFactory.CreateLogger("Bootstrap");

VigilConfig vigilConfig;
byte[] signingKey;
try
{
    vigilConfig = VigilConfig.LoadFromFile(configPath);
    bootstrapLogger.LogInformation("Wczytano vigil.toml: server={Server}", vigilConfig.ServerUrl);

    var dpapiLogger = bootstrapLoggerFactory.CreateLogger<DpapiKeyStore>();
    var dpapi = new DpapiKeyStore(signingPath, dpapiLogger);
    signingKey = dpapi.BootstrapOrLoad(args);
    bootstrapLogger.LogInformation("Signing key załadowany ({Bytes}B)", signingKey.Length);
}
catch (Exception ex)
{
    bootstrapLogger.LogCritical(ex, "Bootstrap failed — Vigil się nie uruchomi");
    throw;
}

var builder = Host.CreateApplicationBuilder(args);

builder.Logging.ClearProviders();
builder.Logging.AddConsole();
builder.Logging.AddEventLog(eventLog =>
{
    eventLog.SourceName = "Vigil Watchdog";
    eventLog.LogName = "Application";
});

builder.Services.AddWindowsService(options =>
{
    options.ServiceName = "vigil_watchdog";
});
// Replaces the default lifetime registered above (last registration wins).
if (WindowsServiceHelpers.IsWindowsService())
    builder.Services.AddSingleton<IHostLifetime, VigilServiceLifetime>();

builder.Services.AddSingleton(vigilConfig);
builder.Services.AddSingleton(sp => new TotpSecretStore(
    Path.Combine(ProgramDataRoot, "totp.dat"), sp.GetRequiredService<ILogger<TotpSecretStore>>()));
builder.Services.AddSingleton<OfflineUnlock>();
builder.Services.AddSingleton(sp => new HeartbeatClient(
    vigilConfig, signingKey, sp.GetRequiredService<ILogger<HeartbeatClient>>()));
builder.Services.AddSingleton<SessionDetector>();

var lockscreenExe = Path.Combine(ProgramDataRoot, "vigil_lockscreen.exe");
builder.Services.AddSingleton(new LockscreenManagerOptions { LockscreenExePath = lockscreenExe });
builder.Services.AddSingleton<NamedPipeIpcServer>();
builder.Services.AddSingleton<LockscreenProcessLauncher>();
builder.Services.AddSingleton<ILockscreenManager, RealLockscreenManager>();

builder.Services.AddHostedService<Worker>();

var host = builder.Build();
await host.RunAsync();
