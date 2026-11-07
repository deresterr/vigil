using System.Globalization;
using System.Runtime.Versioning;
using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Vigil.Watchdog.Configuration;
using Vigil.Watchdog.Detection;
using Vigil.Watchdog.Ui;
using Vigil.Watchdog.Offline;
using Vigil.Watchdog.Server;

namespace Vigil.Watchdog;

/// <summary>
/// Główna pętla watchdoga. Co 5s:
/// 1. Zbierz session/idle/foreground state
/// 2. Wyślij heartbeat
/// 3. Aplikuj response (lock/unlock, aktualizuj tray)
/// 4. Fail-closed jeśli brak heartbeatu > grace_seconds
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
    // True while the lock on screen is our own fail-closed lock (server unreachable): TOTP codes
    // are then verified locally (#9) instead of being forwarded to the server.
    private volatile bool _offlineLocked;

    private readonly DateTime _startTime = DateTime.UtcNow;

    // Tamper telemetry: identity of this process + awake clock at its start (server/app/tamper.py).
    private readonly string _instanceId = Guid.NewGuid().ToString("N");
    private readonly long _awakeMsAtStart = AwakeClock.NowMs;
    private int _failedAttempts;
    private DateTime _lastSuccessfulHeartbeat = DateTime.MinValue;

    // Tick interval:
    //  - normalny (sesja aktywna lub nie zalogowany) — 5s. Server odbiera heartbeat, akumuluje czas.
    //  - gdy lockscreen JEST AKTYWNY — 1s. Krótszy bo:
    //     * po unlock chcemy szybko zauważyć (dziecko nie powinno czekać 5s)
    //     * jeśli vigil_ui zniknie, respawn nastąpi pod sekundę zamiast 5s nieosłoniętego desktopu
    private static readonly TimeSpan _tickIntervalNormal = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan _tickIntervalLocked = TimeSpan.FromSeconds(1);
    // Adaptive interval sugerowany przez serwer (next_check_seconds).
    // Gdy sesja nieaktywna serwer odpowiada 30s — redukuje load gdy dziecko niezalogowane.
    private TimeSpan _tickIntervalAdaptive = TimeSpan.FromSeconds(5);

    public Worker(
        VigilConfig config,
        HeartbeatClient client,
        SessionDetector sessions,
        IUiManager ui,
        TotpSecretStore totpStore,
        OfflineUnlock offline,
        ILogger<Worker> logger)
    {
        _config = config;
        _client = client;
        _sessions = sessions;
        _ui = ui;
        _totpStore = totpStore;
        _offline = offline;
        _logger = logger;

        _ui.OnTotpAttempt += HandleTotpFromLockscreenAsync;
        _ui.OnRetryRequest += HandleRetryRequestAsync;
        _ui.OnResumeFromPause += HandleResumeFromPauseAsync;
        _ui.OnPauseToggle += HandlePauseToggleAsync;
    }

    private readonly SemaphoreSlim _retrySignal = new(0, 1);

    private Task HandleRetryRequestAsync(CancellationToken ct)
    {
        try { _retrySignal.Release(); } catch (SemaphoreFullException) { }
        return Task.CompletedTask;
    }

    private async Task HandleResumeFromPauseAsync(CancellationToken ct)
    {
        try
        {
            await _client.SendPauseToggleAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Nie udało się wysłać pause_toggle (resume from lockscreen)");
        }
    }

    private async Task HandlePauseToggleAsync(CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(8));
        try
        {
            var result = await _client.SendPauseToggleAsync(cts.Token).ConfigureAwait(false);
            _logger.LogInformation("Pause toggle from tray: paused={Paused}", result.Paused);
            try { _retrySignal.Release(); } catch (SemaphoreFullException) { }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Nie udało się wysłać pause_toggle (z tray)");
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Vigil uruchomiony. Server: {Server}", _config.ServerUrl);

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
                // Nigdy nie pozwalamy crash z tick zabić cały service. Log + continue.
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
                // Czekaj N sekund ALBO do sygnału retry z lockscreen — co przyjdzie pierwsze.
                // Po wyjściu z WaitAsync semafor jest "zerowany" (skonsumowany), kolejny
                // retry musi wywołać Release ponownie.
                // Krótszy interval gdy lockscreen jest aktywny — szybszy unlock + szybszy respawn
                // jeśli dziecko zabije process.
                var interval = _ui.IsLocked ? _tickIntervalLocked : _tickIntervalAdaptive;
                await _retrySignal.WaitAsync(interval, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { break; }
        }

        _logger.LogInformation("Vigil zatrzymany.");
    }

    private HeartbeatRequest BuildRequest(bool sessionActive, bool userActive) => new()
    {
        SessionActive = sessionActive,
        UserActive = userActive,
        CurrentTimeUtc = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture),
        UptimeSeconds = (int)(DateTime.UtcNow - _startTime).TotalSeconds,
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

        // Brak idle/foreground tracking — server akumuluje czas gdy sesja aktywna i heartbeat dochodzi.
        var userActive = sessionActive;

        var req = BuildRequest(sessionActive, userActive);

        HeartbeatResponse? response = null;
        bool serverOk = false;
        try
        {
            response = await _client.SendHeartbeatAsync(req, ct).ConfigureAwait(false);
            serverOk = true;
            _lastSuccessfulHeartbeat = DateTime.UtcNow;
            _failedAttempts = 0;
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
            _logger.LogWarning(ex,
                "Heartbeat failed (last success: {Ago:F0}s temu)",
                (DateTime.UtcNow - _lastSuccessfulHeartbeat).TotalSeconds);
        }

        if (serverOk && response is not null)
        {
            // Adaptuj interwał do sugestii serwera gdy nie locked.
            // Serwer wysyła 30s gdy sesja nieaktywna — redukuje load gdy dziecko niezalogowane.
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
    /// Fail-closed (invariant): lock once the server has been silent longer than the grace period
    /// (90 s after service start, 30 s otherwise). Called after a failed heartbeat and after any
    /// tick that threw, so no exception path can skip it. An active offline TOTP grant (#9) is the
    /// only thing that keeps the PC unlocked meanwhile.
    /// </summary>
    private async Task FailClosedIfOverdueAsync(CancellationToken ct)
    {
        var graceSeconds = (DateTime.UtcNow - _startTime).TotalSeconds < 90 ? 90 : 30;
        var timeSinceLastSuccess = (DateTime.UtcNow - _lastSuccessfulHeartbeat).TotalSeconds;
        if (timeSinceLastSuccess > graceSeconds && _offline.IsGrantActive)
        {
            // Parent unlocked with a TOTP code while offline (#9): stay unlocked until the grant ends.
            if (_offlineLocked)
                await _ui.DismissAsync(ct).ConfigureAwait(false);
            _offlineLocked = false;
        }
        else if (timeSinceLastSuccess > graceSeconds)
        {
            // Fail-closed: server unreachable + grace minął
            var canUnlock = _offline.HasSecret;
            _offlineLocked = true;
            await _ui.EnsureLockedAsync(new LockState(
                Reason: "server_unreachable",
                MainMessage: canUnlock
                    ? "Brak połączenia z serwerem rodzica. Rodzic może odblokować komputer kodem z telefonu."
                    : "Brak połączenia z serwerem rodzica. Komputer odblokuje się po przywróceniu połączenia.",
                ShowTotpField: canUnlock,
                ShowResumeButton: false,
                ShowRetryButton: true,
                ShowShutdownButton: true,
                MinutesRemaining: 0,
                CurrentTimeDisplay: DateTime.Now.ToString("HH:mm")),
                ct).ConfigureAwait(false);
        }
        // Inaczej: w grace period, nic nie rób, czekaj na recovery
    }

    private async Task ApplyResponseAsync(HeartbeatResponse response, CancellationToken ct)
    {
        _offlineLocked = false;
        if (response.LockRequired)
        {
            var ui = response.UiState;
            var isPause = response.LockReason == "pause";
            await _ui.EnsureLockedAsync(new LockState(
                Reason: response.LockReason ?? "unknown",
                MainMessage: ui?.MainMessage,
                ShowTotpField: ui?.ShowTotpField ?? false,
                ShowResumeButton: ui?.ShowResumeButton ?? false,
                ShowRetryButton: ui?.ShowRetryButton ?? false,
                ShowShutdownButton: ui?.ShowShutdownButton ?? true,
                MinutesRemaining: response.MinutesRemaining,
                CurrentTimeDisplay: ui?.CurrentTimeDisplay),
                ct).ConfigureAwait(false);

            // Tray: pauza aktywna → "paused" icon; inne blokady → "locked" icon
            await _ui.SendTrayStateAsync(paused: isPause, canPause: false, response.MinutesRemaining, ct)
                .ConfigureAwait(false);
            return;
        }

        await _ui.DismissAsync(ct).ConfigureAwait(false);
        await _ui.SendTrayStateAsync(paused: false, canPause: true, response.MinutesRemaining, ct)
            .ConfigureAwait(false);

    }

    /// <summary>
    /// Forward TOTP attempt z lockscreen UI do servera, zwróć rezultat z powrotem do UI.
    /// </summary>
    private async Task<TotpResult> HandleTotpFromLockscreenAsync(
        TotpFromLockscreen attempt, CancellationToken ct)
    {
        if (_offlineLocked)
        {
            var offline = _offline.TryUnlock(attempt.Code, attempt.MinutesToGrant);
            if (offline.Ok)
            {
                try { _retrySignal.Release(); } catch (SemaphoreFullException) { }  // dismiss on the next tick now
            }
            return offline;
        }

        try
        {
            var resp = await _client.SendTotpAttemptAsync(new TotpAttemptRequest
            {
                Code = attempt.Code,
                MinutesToGrant = attempt.MinutesToGrant,
            }, ct).ConfigureAwait(false);

            return new TotpResult(resp.Ok, resp.Mode, resp.Applied, resp.Reason, resp.LockoutSecondsRemaining);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "TOTP attempt failed");
            return new TotpResult(false, null, null, "network_error");
        }
    }
}
