using System.Diagnostics;
using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;
using Vigil.Watchdog.Detection;
using Vigil.Watchdog.Native;
using Vigil.Watchdog.Server;

namespace Vigil.Watchdog.Ui;

/// <summary>
/// Keeps vigil_ui running in the child's session and drives it over \\.\pipe\VigilUI (D8).
///
/// - EnsureRunningAsync (every tick): spawn when missing, wait for its pipe connect, re-send the
///   current lock and tray state. A respawned UI starts unlocked, so the lock must be re-applied.
/// - Lock / unlock / tray are messages to the resident process: no spawn on lock, nothing to kill
///   on unlock, the UI restores the taskbar itself.
/// - Kill loop (#10): a UI we started that disappears on its own 3× within 60 s, locked or not,
///   logs the session off and reports tamper_lockscreen_killed. Friction + detection: the process
///   DACL already stops non-elevated killers (UiProcessLauncher); an elevated admin can stop the
///   watchdog itself, which the server reports (#7).
///
/// Not thread-safe: called from the Worker loop only. Pipe messages arrive on the reader task and
/// only raise the events.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class UiManager : IUiManager, IDisposable
{
    private const int KillsBeforeLogoff = 3;
    private static readonly TimeSpan KillWindow = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(10);

    private readonly NamedPipeIpcServer _pipe;
    private readonly UiProcessLauncher _launcher;
    private readonly SessionDetector _sessions;
    private readonly UiManagerOptions _options;
    private readonly HeartbeatClient _client;
    private readonly ILogger<UiManager> _logger;

    private readonly Queue<DateTime> _unexpectedExits = new();
    private bool _expectedRunning;   // we started it and did not tell it to exit
    private uint? _uiSession;
    private string? _lastSpawnError;  // log a failing spawn once, not every tick
    private bool _locked;
    private IpcSetState? _lockState;
    private IpcTrayState? _trayState;
    private bool _disposed;

    public bool IsLocked => _locked && _pipe.IsConnected;

    public event Func<TotpFromLockscreen, CancellationToken, Task<TotpResult>>? OnTotpAttempt;
    public event Func<CancellationToken, Task>? OnRetryRequest;
    public event Func<CancellationToken, Task>? OnResumeFromPause;
    public event Func<CancellationToken, Task>? OnPauseToggle;

    public UiManager(
        NamedPipeIpcServer pipe,
        UiProcessLauncher launcher,
        SessionDetector sessions,
        UiManagerOptions options,
        HeartbeatClient client,
        ILogger<UiManager> logger)
    {
        _pipe = pipe;
        _launcher = launcher;
        _sessions = sessions;
        _options = options;
        _client = client;
        _logger = logger;
        _pipe.MessageReceived += HandleIncomingAsync;
    }

    public async Task<bool> EnsureRunningAsync(CancellationToken ct)
    {
        var (active, sessionId) = _sessions.DetectActiveSession();
        if (!active || sessionId is null)
        {
            // Logged off or not logged on yet: the UI went with the session, nobody killed it.
            ForgetProcess();
            return false;
        }
        if (_uiSession == sessionId && _launcher.IsRunning && _pipe.IsConnected) return true;

        if (_uiSession != sessionId)
            ForgetProcess();  // a new logon; whatever ran before went with its session
        else if (_expectedRunning && !_launcher.IsRunning
                 && await HandleUnexpectedExitAsync(sessionId.Value, ct).ConfigureAwait(false))
            return false;     // session logged off

        if (!await SpawnAndConnectAsync(sessionId.Value, ct).ConfigureAwait(false)) return false;

        if (_locked && _lockState is not null) await TrySendAsync(_lockState, ct).ConfigureAwait(false);
        if (_trayState is not null) await TrySendAsync(_trayState, ct).ConfigureAwait(false);
        return true;
    }

    private async Task<bool> SpawnAndConnectAsync(uint sessionId, CancellationToken ct)
    {
        var pidSource = new TaskCompletionSource<uint>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        // The pipe must exist before the process starts; it is verified against the PID afterwards.
        var connect = _pipe.WaitForClientAsync(pidSource.Task, connectCts.Token);

        uint pid;
        try
        {
            pid = _launcher.Launch(_options.UiExePath, sessionId);
            pidSource.SetResult(pid);
        }
        catch (Exception ex)
        {
            pidSource.TrySetCanceled();
            connectCts.Cancel();
            await ObserveAsync(connect).ConfigureAwait(false);
            if (ex.Message != _lastSpawnError)
                _logger.LogWarning(ex, "Could not start vigil_ui in session {Session}", sessionId);
            _lastSpawnError = ex.Message;
            return false;
        }
        _lastSpawnError = null;
        _uiSession = sessionId;
        // Expected to run from the moment it is spawned: a kill loop usually strikes before the
        // process even connects to the pipe.
        _expectedRunning = true;

        // Wait for the connect, but stop early if the process dies (killed before connecting).
        Task exited;
        Process? proc = null;
        try
        {
            proc = Process.GetProcessById((int)pid);
            exited = proc.WaitForExitAsync(connectCts.Token);
        }
        catch (ArgumentException)
        {
            exited = Task.CompletedTask;  // already gone
        }

        try
        {
            var done = await Task.WhenAny(connect, exited, Task.Delay(ConnectTimeout, ct)).ConfigureAwait(false);
            if (done == connect)
            {
                await connect.ConfigureAwait(false);  // rethrows a failed peer check
                _logger.LogInformation("vigil_ui running (pid={Pid}, session={Session})", pid, sessionId);
                return true;
            }

            connectCts.Cancel();
            await ObserveAsync(connect).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            if (done == exited)
            {
                // Counted as an unexpected exit on the next tick.
                _logger.LogWarning("vigil_ui pid={Pid} exited before connecting to the pipe", pid);
                return false;
            }
            _logger.LogError("vigil_ui pid={Pid} did not connect within {Timeout}s, killing it",
                pid, (int)ConnectTimeout.TotalSeconds);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            // UnauthorizedAccessException: another process grabbed the pipe first and ours could not connect.
            _logger.LogWarning(ex, "vigil_ui pid={Pid} did not connect, restarting it on the next tick", pid);
        }
        finally
        {
            proc?.Dispose();
        }
        // Our own kill, not the child's.
        _expectedRunning = false;
        _launcher.Kill();
        return false;
    }

    public async Task EnsureLockedAsync(LockState state, CancellationToken ct)
    {
        _lockState = new IpcSetState
        {
            LockReason = state.Reason,
            ShowTotpField = state.ShowTotpField,
            ShowResumeButton = state.ShowResumeButton,
            ShowRetryButton = state.ShowRetryButton,
            ShowShutdownButton = state.ShowShutdownButton,
            MinutesRemaining = state.MinutesRemaining,
            CurrentTimeDisplay = state.CurrentTimeDisplay,
        };
        if (!_locked) _logger.LogInformation("Lock: {Reason}", state.Reason);
        _locked = true;
        // A fresh spawn already got the state; a duplicate set_state is harmless.
        if (await EnsureRunningAsync(ct).ConfigureAwait(false))
            await TrySendAsync(_lockState, ct).ConfigureAwait(false);
    }

    public async Task DismissAsync(CancellationToken ct)
    {
        if (!_locked) return;
        _locked = false;
        _lockState = null;
        _logger.LogInformation("Unlock");
        await TrySendAsync(new IpcUnlock(), ct).ConfigureAwait(false);
    }

    public async Task SendTrayStateAsync(bool paused, bool canPause, int minutesRemaining, CancellationToken ct)
    {
        _trayState = new IpcTrayState { Paused = paused, CanPause = canPause, MinutesRemaining = minutesRemaining };
        await TrySendAsync(_trayState, ct).ConfigureAwait(false);
    }

    private async Task TrySendAsync(object message, CancellationToken ct)
    {
        if (!_pipe.IsConnected) return;  // re-sent after the next respawn
        try
        {
            await _pipe.SendAsync(message, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            _logger.LogDebug(ex, "Send {Type} to vigil_ui failed", message.GetType().Name);
        }
    }

    private void ForgetProcess()
    {
        _expectedRunning = false;
        _unexpectedExits.Clear();
        _uiSession = null;
    }

    private static async Task ObserveAsync(Task t)
    {
        try { await t.ConfigureAwait(false); }
        catch { /* cancelled or rejected: nothing to do */ }
    }

    /// <summary>
    /// The UI we expected to be running is gone. Returns true if the session was logged off.
    /// </summary>
    private async Task<bool> HandleUnexpectedExitAsync(uint sessionId, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        _unexpectedExits.Enqueue(now);
        while (_unexpectedExits.Count > 0 && now - _unexpectedExits.Peek() > KillWindow)
            _unexpectedExits.Dequeue();
        var count = _unexpectedExits.Count;
        _logger.LogWarning("vigil_ui exited unexpectedly ({Count} in {Window}s), respawning",
            count, (int)KillWindow.TotalSeconds);
        if (count < KillsBeforeLogoff) return false;

        ForgetProcess();
        var loggedOff = Win32.WTSLogoffSession(IntPtr.Zero, sessionId, false);
        if (loggedOff)
            _logger.LogWarning("vigil_ui killed {Count} times — session {Session} logged off", count, sessionId);
        else
            _logger.LogError("vigil_ui killed {Count} times — logoff of session {Session} failed (error {Err})",
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
            _logger.LogWarning(ex, "Could not report the vigil_ui kill loop to the server");
        }
        return loggedOff;
    }

    private async Task HandleIncomingAsync(object message)
    {
        switch (message)
        {
            case IpcTotpAttempt totp:
                {
                    var handler = OnTotpAttempt;
                    if (handler is null) return;
                    var result = await handler(new TotpFromLockscreen(totp.Code, totp.Minutes), CancellationToken.None)
                        .ConfigureAwait(false);
                    await TrySendAsync(new IpcTotpResponse
                    {
                        Ok = result.Ok,
                        Mode = result.Mode,
                        Minutes = result.Minutes,
                        UntilLocal = result.UntilLocal,
                        Reason = result.Reason,
                        LockoutSecondsRemaining = result.LockoutSecondsRemaining,
                    }, CancellationToken.None).ConfigureAwait(false);
                    break;
                }
            case IpcRetryNow:
                if (OnRetryRequest is { } retry) await retry(CancellationToken.None).ConfigureAwait(false);
                break;
            case IpcResumeFromPause:
                if (OnResumeFromPause is { } resume) await resume(CancellationToken.None).ConfigureAwait(false);
                break;
            case IpcPauseToggle:
                if (OnPauseToggle is { } toggle) await toggle(CancellationToken.None).ConfigureAwait(false);
                break;
            case IpcShutdownRequest:
                _logger.LogInformation("vigil_ui asked for a shutdown");
                Process.Start(new ProcessStartInfo
                {
                    FileName = "shutdown.exe",
                    Arguments = "/s /t 0",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                });
                break;
            default:
                _logger.LogWarning("Pipe: unexpected message {Type}", message.GetType().Name);
                break;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        // Service stopping: take the UI (and any lock) down with us.
        _expectedRunning = false;
        try
        {
            if (_pipe.IsConnected)
                _pipe.SendAsync(new IpcExit(), CancellationToken.None).Wait(TimeSpan.FromSeconds(1));
            var deadline = DateTime.UtcNow.AddSeconds(1);
            while (_launcher.IsRunning && DateTime.UtcNow < deadline) Thread.Sleep(100);
        }
        catch { /* best effort on shutdown */ }
        _launcher.Kill();
    }
}

public sealed class UiManagerOptions
{
    public required string UiExePath { get; init; }
}
