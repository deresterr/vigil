using System.Globalization;
using System.Net.NetworkInformation;
using System.Runtime.Versioning;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Vigil.Watchdog.Configuration;
using Vigil.Watchdog.Detection;
using Vigil.Watchdog.Ui;
using Vigil.Watchdog.Offline;
using Vigil.Watchdog.Server;

namespace Vigil.Watchdog;

/// <summary>
/// The watchdog's loop, every tick (docs/architecture.md):
/// 1. vigil_ui running and connected in the active session
/// 2. session state → heartbeat
/// 3. apply the response (lock / unlock, tray)
/// 4. fail-closed when the server has not answered since the start or for longer than the grace period
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class Worker : BackgroundService
{
    private readonly VigilConfig _config;
    private readonly HeartbeatClient _client;
    private readonly SessionDetector _sessions;
    private readonly IUiManager _ui;
    private readonly ILogger<Worker> _logger;
    private readonly TotpSecretStore _totpStore;
    private readonly OfflineUnlock _offline;
    private readonly ServerClock _clock;
    private readonly OutageLog _outage;
    // True while the lock on screen is our own fail-closed lock (server unreachable): TOTP codes
    // are then verified locally (#9) instead of being forwarded to the server.
    private volatile bool _offlineLocked;

    // Durations on the monotonic clock (#92): with the wall clock, an administrator who set it back after the
    // last heartbeat and then blocked the server got a negative "time since last success" — no lock (fail-open).
    private readonly long _startMs = ServerClock.MonotonicMs;

    // Tamper telemetry: identity of this process + awake clock at its start (server/app/tamper.py).
    private readonly string _instanceId = Guid.NewGuid().ToString("N");
    private readonly long _awakeMsAtStart = AwakeClock.NowMs;
    private int _failedAttempts;
    private long? _lastSuccessMs;  // null: never reached the server since the service started
    // The last heartbeat was refused for this computer's key (removed or re-paired in the panel): the offline
    // lock says so instead of "no connection", so the parent knows to pair it again (#120).
    private volatile bool _keyRejected;
    // The last heartbeat failed because this PC has no network at all (Wi-Fi not joined yet): the offline lock
    // says so instead of blaming the server (#128). The message only; the lock is the same.
    private volatile bool _networkDown;

    // Fail-closed (invariant): no answer since the service started, or none for this long, means locked.
    private const double GraceSeconds = 30;

    // Locked: a tick every second, so an unlock shows at once and a vanished vigil_ui is back within a second
    // instead of leaving the desktop open for 5 s.
    private static readonly TimeSpan _tickIntervalLocked = TimeSpan.FromSeconds(1);
    // Unlocked: the server's next_check_seconds (30 s while nobody is logged on), clamped to 5–60 s.
    private TimeSpan _tickIntervalAdaptive = TimeSpan.FromSeconds(5);

    public Worker(
        VigilConfig config,
        HeartbeatClient client,
        SessionDetector sessions,
        IUiManager ui,
        TotpSecretStore totpStore,
        OfflineUnlock offline,
        ServerClock clock,
        ILogger<OutageLog> outageLogger,
        ILogger<Worker> logger)
    {
        _config = config;
        _client = client;
        _sessions = sessions;
        _ui = ui;
        _totpStore = totpStore;
        _offline = offline;
        _clock = clock;
        _outage = new OutageLog("Heartbeat", outageLogger);
        _logger = logger;

        _ui.OnTotpAttempt += HandleTotpFromLockscreenAsync;
        _ui.OnRetryRequest += HandleRetryRequestAsync;
        _ui.OnResumeFromPause += HandleResumeFromPauseAsync;
        _ui.OnPauseToggle += HandlePauseToggleAsync;
        // A logon: vigil_ui and any lock at once, not after the idle interval of an empty PC (30–60 s).
        VigilServiceLifetime.SessionLoggedOn += WakeUp;
        // A new address after a failed heartbeat (Wi-Fi joined, cable in): try now, so a lock waiting for the
        // network goes as soon as it is there. Not while the server answers: addresses change for other reasons too.
        NetworkChange.NetworkAddressChanged += (_, _) =>
        {
            if (Volatile.Read(ref _failedAttempts) > 0) WakeUp();
        };
    }

    private readonly SemaphoreSlim _retrySignal = new(0, 1);

    /// <summary>Starts the next tick now (each Release wakes one wait; a pending wake-up is enough).</summary>
    private void WakeUp()
    {
        try { _retrySignal.Release(); } catch (SemaphoreFullException) { }
    }

    private Task HandleRetryRequestAsync(CancellationToken ct)
    {
        WakeUp();
        return Task.CompletedTask;
    }

    private async Task HandleResumeFromPauseAsync(CancellationToken ct)
    {
        try
        {
            // A target, not a toggle: a double click (or one per monitor) must not start the pause again.
            await _client.SendPauseToggleAsync(paused: false, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "pause_toggle failed (resume from the lock screen)");
        }
    }

    private async Task HandlePauseToggleAsync(CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(8));
        try
        {
            var result = await _client.SendPauseToggleAsync(paused: null, cts.Token).ConfigureAwait(false);
            _logger.LogInformation("Pause toggle from tray: paused={Paused}", result.Paused);
            WakeUp();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "pause_toggle failed (from the tray)");
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Vigil started. Server: {Server}", _config.ServerUrl);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await TickAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // A failed tick never ends the service.
                _logger.LogError(ex, "Tick failed");
                // Fail-closed must not depend on the tick finishing: whatever broke it, still lock
                // once the server has been silent for longer than the grace period.
                try { await FailClosedIfOverdueAsync(stoppingToken).ConfigureAwait(false); }
                catch (Exception lockEx) when (!stoppingToken.IsCancellationRequested)
                {
                    _logger.LogError(lockEx, "Fail-closed lock after a failed tick failed");
                }
            }

            try
            {
                // The interval, or earlier when the lock screen asks to retry (each Release wakes one wait).
                var interval = _ui.IsLocked ? _tickIntervalLocked : _tickIntervalAdaptive;
                await _retrySignal.WaitAsync(interval, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { break; }
        }

        _logger.LogInformation("Vigil stopped");
    }

    private HeartbeatRequest BuildRequest(bool sessionActive, bool userActive) => new()
    {
        SessionActive = sessionActive,
        UserActive = userActive,
        // The PC's own clock on purpose (not ServerClock): the server journals its drift (clock_drift).
        CurrentTimeUtc = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture),
        UptimeSeconds = (int)SecondsSince(_startMs),
        InstanceId = _instanceId,
        AwakeMs = AwakeClock.NowMs,
        AwakeMsAtStart = _awakeMsAtStart,
        FailedAttempts = _failedAttempts,
        TotpFp = _totpStore.Fingerprint,
        OfflineGrants = _offline.PendingGrants(),
    };

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        // Tell the server why we are going away; detection, best effort (network may already be down).
        var reason = VigilServiceLifetime.IsSystemShutdown ? "shutdown" : "service_stop";
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromSeconds(3));
            await _client.SendGoodbyeAsync(new GoodbyeRequest { InstanceId = _instanceId, Reason = reason }, cts.Token)
                .ConfigureAwait(false);
            _logger.LogInformation("Goodbye sent ({Reason})", reason);
        }
        catch (KeyRejectedException)
        {
            // Re-pairing replaces the key on the server before the installer stops this service: the
            // server already knows, nothing to warn about.
            _logger.LogInformation("Goodbye refused: the key was replaced or the device removed ({Reason})", reason);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Goodbye not delivered ({Reason})", reason);
        }
        await base.StopAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task TickAsync(CancellationToken ct)
    {
        // vigil_ui first: it must be up (tray, and ready to lock) whether or not the server answers.
        await _ui.EnsureRunningAsync(ct).ConfigureAwait(false);

        var (sessionActive, _) = _sessions.DetectActiveSession();

        // No idle detection (D16): the server counts time while a session is active and heartbeats arrive.
        var userActive = sessionActive;

        var req = BuildRequest(sessionActive, userActive);

        HeartbeatResponse? response = null;
        bool serverOk = false;
        try
        {
            response = await _client.SendHeartbeatAsync(req, ct).ConfigureAwait(false);
            serverOk = true;
            _lastSuccessMs = ServerClock.MonotonicMs;
            _failedAttempts = 0;
            _keyRejected = false;
            _networkDown = false;
            _outage.Success();
            _offline.OnServerReachable(req.OfflineGrants);
            if (!string.IsNullOrEmpty(response.TotpSecret))
            {
                try { _totpStore.Save(response.TotpSecret); }
                catch (Exception ex) { _logger.LogWarning(ex, "Could not store the TOTP secret for offline unlock"); }
            }
        }
        // Filter on our own stop token, not on the exception type: an HttpClient timeout is a
        // TaskCanceledException too, and letting it escape skipped the fail-closed check (#31).
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            _failedAttempts++;
            _keyRejected = ex is KeyRejectedException;
            _networkDown = OutageLog.IsNoNetwork(ex);
            // One line per failure outside the Event Log; OutageLog writes there when it lasts (#128).
            _logger.LogInformation(
                "Heartbeat failed: {Error} (last success {Ago})", ex.Message,
                _lastSuccessMs is { } at ? $"{SecondsSince(at):F0} s ago" : "never since the service started");
            _outage.Failure(ex);
        }

        if (serverOk && response is not null)
        {
            if (!_ui.IsLocked && response.NextCheckSeconds > 0)
                _tickIntervalAdaptive = TimeSpan.FromSeconds(
                    Math.Clamp(response.NextCheckSeconds, 5, 60));

            await ApplyResponseAsync(response, ct).ConfigureAwait(false);
        }
        else
        {
            await FailClosedIfOverdueAsync(ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Fail-closed (invariant): lock when the server has not answered since the service started, or not for longer
    /// than the grace period — on the monotonic clock, which counts sleep: a PC woken up (also from a shutdown with
    /// Fast Startup, which hibernates the service) is locked until the server answers. Called after a failed
    /// heartbeat and after any tick that threw, so no exception path can skip it. An active offline TOTP grant (#9)
    /// is the only thing that keeps the PC unlocked meanwhile.
    /// </summary>
    private async Task FailClosedIfOverdueAsync(CancellationToken ct)
    {
        var overdue = _lastSuccessMs is not { } at || SecondsSince(at) > GraceSeconds;
        if (overdue && _offline.IsGrantActive)
        {
            // Parent unlocked with a TOTP code while offline (#9): stay unlocked until the grant ends.
            if (_offlineLocked)
                await _ui.DismissAsync(ct).ConfigureAwait(false);
            _offlineLocked = false;
        }
        else if (overdue)
        {
            var canUnlock = _offline.HasSecret;
            _offlineLocked = true;
            await _ui.EnsureLockedAsync(new LockState(
                // This watchdog's own locks (codes, worded by vigil_ui); the server knows none of them.
                Reason: _keyRejected ? "device_unpaired" : _networkDown ? "network_down" : "server_unreachable",
                ShowTotpField: canUnlock,  // vigil_ui words the message by it: code from the phone or wait
                ShowResumeButton: false,
                ShowRetryButton: true,
                MinutesRemaining: 0,
                CurrentTimeDisplay: _clock.LocalNow.ToString("HH:mm")),
                ct).ConfigureAwait(false);
        }
        // Otherwise: still within the grace period.
    }

    private static double SecondsSince(long monotonicMs) => (ServerClock.MonotonicMs - monotonicMs) / 1000.0;

    private async Task ApplyResponseAsync(HeartbeatResponse response, CancellationToken ct)
    {
        _offlineLocked = false;
        if (response.LockRequired)
        {
            var ui = response.UiState;
            await _ui.EnsureLockedAsync(new LockState(
                Reason: response.LockReason ?? "unknown",
                ShowTotpField: ui?.ShowTotpField ?? false,
                ShowResumeButton: ui?.ShowResumeButton ?? false,
                ShowRetryButton: false,  // retrying is for our own offline lock only
                MinutesRemaining: response.MinutesRemaining,
                CurrentTimeDisplay: ui?.CurrentTimeDisplay,
                NightEndLocal: ui?.NightEndLocal),
                ct).ConfigureAwait(false);
            return;
        }

        await _ui.DismissAsync(ct).ConfigureAwait(false);
        await _ui.SendTrayStateAsync(response.MinutesRemaining, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// A TOTP code from the lock screen: to the server, or checked here while locked offline (#9).
    /// </summary>
    private async Task<TotpResult> HandleTotpFromLockscreenAsync(
        TotpFromLockscreen attempt, CancellationToken ct)
    {
        if (_offlineLocked)
        {
            var offline = _offline.TryUnlock(attempt.Code, attempt.MinutesToGrant);
            if (offline.Ok) WakeUp();  // dismiss on the next tick now
            return offline;
        }

        try
        {
            var resp = await _client.SendTotpAttemptAsync(new TotpAttemptRequest
            {
                Code = attempt.Code,
                MinutesToGrant = attempt.MinutesToGrant,
            }, ct).ConfigureAwait(false);

            return new TotpResult(resp.Ok, resp.Mode, resp.Minutes, resp.UntilLocal, resp.Reason, resp.LockoutSecondsRemaining);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "TOTP attempt failed");
            return new TotpResult(false, Reason: "network_error");
        }
    }
}
