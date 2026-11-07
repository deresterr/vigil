using System.Diagnostics;
using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;
using Vigil.Watchdog.Detection;
using Vigil.Watchdog.Native;
using Vigil.Watchdog.Server;

namespace Vigil.Watchdog.Lockscreen;

/// <summary>
/// Real implementacja ILockscreenManager. Łączy:
/// - LockscreenProcessLauncher (spawn vigil_lockscreen.exe w user session)
/// - NamedPipeIpcServer (komunikacja z lockscreen)
///
/// Sekwencja przy EnsureLockedAsync:
/// 1. Jeśli lockscreen już running + pipe connected → tylko send updated set_state
/// 2. Inaczej:
///    a. Spawn lockscreen (CreateProcessAsUser)
///    b. Wait for pipe connection (z timeout)
///    c. Send set_state
///
/// DismissAsync: send IpcExit przez pipe → lockscreen sam się zamknie. Backup: kill po 2s.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class RealLockscreenManager : ILockscreenManager, IDisposable
{
    private readonly NamedPipeIpcServer _pipe;
    private readonly LockscreenProcessLauncher _launcher;
    private readonly SessionDetector _sessions;
    private readonly LockscreenManagerOptions _options;
    private readonly ILogger<RealLockscreenManager> _logger;

    private CancellationTokenSource? _connectCts;
    private Task? _connectTask;
    private bool _disposed;

    // Kill-loop detection (#10). A lock we spawned and did not dismiss should keep running; if it
    // exits on its own repeatedly (a script killing it the moment it appears), log the session off.
    // Friction + detection: an elevated attacker can still stop the watchdog itself, which #7 reports.
    private const int KillsBeforeLogoff = 3;
    private static readonly TimeSpan KillWindow = TimeSpan.FromSeconds(60);
    private readonly HeartbeatClient _client;
    private readonly Queue<DateTime> _unexpectedExits = new();
    private bool _expectedRunning;

    public bool IsLocked => _launcher.IsRunning;

    public event Func<TotpFromLockscreen, CancellationToken, Task<TotpResult>>? OnTotpAttempt;
    public event Func<CancellationToken, Task>? OnRetryRequest;
    public event Func<CancellationToken, Task>? OnResumeFromPause;

    public RealLockscreenManager(
        NamedPipeIpcServer pipe,
        LockscreenProcessLauncher launcher,
        SessionDetector sessions,
        LockscreenManagerOptions options,
        HeartbeatClient client,
        ILogger<RealLockscreenManager> logger)
    {
        _client = client;
        _pipe = pipe;
        _launcher = launcher;
        _sessions = sessions;
        _options = options;
        _logger = logger;

        _pipe.MessageReceived += HandleIncomingAsync;
    }

    public async Task EnsureLockedAsync(LockState state, CancellationToken ct)
    {
        // Konwersja LockState (manager-level) → IpcSetState (wire format)
        var setState = new IpcSetState
        {
            LockReason = state.Reason,
            MainMessage = state.MainMessage,
            ShowTotpField = state.ShowTotpField,
            ShowResumeButton = state.ShowResumeButton,
            ShowRetryButton = state.ShowRetryButton,
            ShowShutdownButton = state.ShowShutdownButton,
            MinutesRemaining = state.MinutesRemaining,
            CurrentTimeDisplay = state.CurrentTimeDisplay,
        };

        if (_launcher.IsRunning && _pipe.IsConnected)
        {
            await _pipe.SendAsync(setState, ct).ConfigureAwait(false);
            return;
        }

        // Need to (re)spawn
        var (active, sessionId) = _sessions.DetectActiveSession();
        if (!active || sessionId is null)
        {
            _logger.LogWarning(
                "EnsureLocked: nie ma aktywnej sesji użytkownika — nie mogę spawnować lockscreen.");
            return;
        }

        if (_expectedRunning && await HandleUnexpectedExitAsync(sessionId.Value, ct).ConfigureAwait(false))
            return;  // session logged off; nothing to lock until the next logon

        // Czyść stary state jeśli był (na wypadek crashu lockscreen)
        _launcher.Kill();
        await _pipe.DisconnectAsync().ConfigureAwait(false);

        // Lockscreen idzie na Default desktop dziecka (WPF wymaga DWM którego custom
        // CreateDesktop nie ma). Bezpieczeństwo realizuje KeyboardBlocker (Win/Alt+Tab/Alt+F4/Ctrl+Esc).
        _connectCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _connectTask = _pipe.WaitForClientAsync(_connectCts.Token, _options.LockscreenExePath);

        var desktopName = "Default";

        try
        {
            _launcher.Launch(_options.LockscreenExePath, sessionId.Value, desktopName);
            // Expected to run from the moment it is spawned: a kill loop usually strikes before the
            // lockscreen even connects to the pipe.
            _expectedRunning = true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Nie udało się spawnować vigil_lockscreen");
            _connectCts?.Cancel();
            return;
        }

        // Wait for pipe connection — max 10s
        try
        {
            await _connectTask.WaitAsync(TimeSpan.FromSeconds(10), ct).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            _logger.LogError("Lockscreen spawned ale nie połączył się przez pipe w 10s — kill");
            _launcher.Kill();
            return;
        }
        catch (UnauthorizedAccessException)
        {
            // Peer verification failed — coś podłączyło się do pipe co nie jest naszym vigil_lockscreen.
            // Kill spawned lockscreen i exit; następny tick spróbuje od nowa.
            _logger.LogWarning("Pipe peer verification failed — kill lockscreen, retry next tick");
            _launcher.Kill();
            return;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return;
        }

        await _pipe.SendAsync(setState, ct).ConfigureAwait(false);
        _logger.LogInformation("Lockscreen aktywny, state={Reason}, desktop={Desktop}",
            state.Reason, desktopName);
    }

    public async Task DismissAsync(CancellationToken ct)
    {
        _expectedRunning = false;
        _unexpectedExits.Clear();
        if (!_launcher.IsRunning) return;

        _logger.LogInformation("Dismissing lockscreen");

        // Graceful: send exit
        if (_pipe.IsConnected)
        {
            try
            {
                await _pipe.SendAsync(new IpcExit(), ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Send exit message failed — przejdę do kill");
            }
        }

        // Wait 1s for graceful exit (helper spawnowany poniżej gwarantuje restore paska
        // niezależnie od tego czy exit był graceful czy Kill — nie trzeba długo czekać)
        var deadline = DateTime.UtcNow.AddSeconds(1);
        while (_launcher.IsRunning && DateTime.UtcNow < deadline)
        {
            await Task.Delay(100, ct).ConfigureAwait(false);
        }

        if (_launcher.IsRunning)
        {
            _launcher.Kill();
        }

        await _pipe.DisconnectAsync().ConfigureAwait(false);

        // Failsafe: zawsze spawnuj restore-taskbar-only helper w user session po zakończeniu
        // lockscreen — niezależnie od tego czy exit był graceful czy Kill().
        // Watchdog działa jako SYSTEM (Session 0) i nie może bezpośrednio wywołać ShowWindow
        // na oknach w sesji użytkownika (Session 0 Isolation). Helper robi to z właściwego
        // kontekstu sesji. Przy graceful exit jest no-op (pasek już przywrócony przez OnExit),
        // przy Kill() jest to jedyna szansa na przywrócenie.
        try
        {
            var (_, sessionId) = _sessions.DetectActiveSession();
            if (sessionId is not null)
            {
                _launcher.Launch(
                    _options.LockscreenExePath, sessionId.Value,
                    "Default", "--restore-taskbar-only", trackProcess: false);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Nie udało się uruchomić restore-taskbar helper — pasek może zostać ukryty");
        }
    }

    /// <summary>
    /// The lockscreen we expected to be running is gone. Returns true if the session was logged off.
    /// </summary>
    private async Task<bool> HandleUnexpectedExitAsync(uint sessionId, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        _unexpectedExits.Enqueue(now);
        while (_unexpectedExits.Count > 0 && now - _unexpectedExits.Peek() > KillWindow)
            _unexpectedExits.Dequeue();
        var count = _unexpectedExits.Count;
        _logger.LogWarning("Lockscreen exited unexpectedly ({Count} in {Window}s), respawning",
            count, (int)KillWindow.TotalSeconds);
        if (count < KillsBeforeLogoff) return false;

        _unexpectedExits.Clear();
        _expectedRunning = false;
        var loggedOff = Win32.WTSLogoffSession(IntPtr.Zero, sessionId, false);
        if (loggedOff)
            _logger.LogWarning("Lockscreen killed {Count} times — session {Session} logged off", count, sessionId);
        else
            _logger.LogError("Lockscreen killed {Count} times — logoff of session {Session} failed (error {Err})",
                count, sessionId, System.Runtime.InteropServices.Marshal.GetLastPInvokeError());

        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(5));
            await _client.SendTamperEventAsync(new TamperEventRequest
            {
                Type = "lockscreen_killed",
                Kills = count,
                WindowSeconds = (int)KillWindow.TotalSeconds,
                LoggedOff = loggedOff,
            }, cts.Token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Offline: only the Event Log has it. Detection, best effort.
            _logger.LogWarning(ex, "Could not report lockscreen kill-loop to the server");
        }
        return loggedOff;
    }

    /// <summary>
    /// Handler dla messages przychodzących z lockscreen. Dispatch do eventów ILockscreenManager.
    /// </summary>
    private async Task HandleIncomingAsync(object message)
    {
        switch (message)
        {
            case IpcTotpAttempt totp:
                {
                    var handler = OnTotpAttempt;
                    if (handler is null)
                    {
                        _logger.LogWarning("TOTP attempt z lockscreen ale brak handlera");
                        return;
                    }
                    var result = await handler(
                        new TotpFromLockscreen(totp.Code, totp.Minutes),
                        CancellationToken.None).ConfigureAwait(false);

                    // Wyślij response z powrotem do lockscreen
                    if (_pipe.IsConnected)
                    {
                        await _pipe.SendAsync(new IpcTotpResponse
                        {
                            Ok = result.Ok,
                            Mode = result.Mode,
                            Applied = result.Applied,
                            Reason = result.Reason,
                            LockoutSecondsRemaining = result.LockoutSecondsRemaining,
                        }, CancellationToken.None).ConfigureAwait(false);
                    }
                    break;
                }
            case IpcRetryNow:
                {
                    var handler = OnRetryRequest;
                    if (handler is not null)
                        await handler(CancellationToken.None).ConfigureAwait(false);
                    break;
                }
            case IpcResumeFromPause:
                {
                    var handler = OnResumeFromPause;
                    if (handler is not null)
                        await handler(CancellationToken.None).ConfigureAwait(false);
                    break;
                }
            case IpcShutdownRequest:
                _logger.LogInformation("Lockscreen poprosił o shutdown — wykonuję");
                Process.Start(new ProcessStartInfo
                {
                    FileName = "shutdown.exe",
                    Arguments = "/s /t 0",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                });
                break;
            default:
                _logger.LogWarning("Pipe: nieobsługiwany message type {Type}", message.GetType().Name);
                break;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        try { DismissAsync(CancellationToken.None).GetAwaiter().GetResult(); }
        catch { /* ignore on dispose */ }
        _connectCts?.Dispose();
        _disposed = true;
    }
}

/// <summary>Konfiguracja RealLockscreenManager — głównie ścieżka do vigil_lockscreen.exe.</summary>
public sealed class LockscreenManagerOptions
{
    public required string LockscreenExePath { get; init; }
}
