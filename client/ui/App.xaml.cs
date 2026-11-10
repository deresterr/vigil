using System.Runtime.Versioning;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Win32;
using Vigil.Ui.Native;
using Vigil.Ui.ViewModels;
// Only Screen from WinForms: the whole namespace clashes with WPF (Application, KeyEventArgs, ...).
using WinFormsScreen = System.Windows.Forms.Screen;

namespace Vigil.Ui;

/// <summary>
/// vigil_ui: the single Vigil process in the child's session (D8). Started and kept alive by the
/// watchdog (UiManager), it shows the tray icon all the time and the lock overlay on demand:
///   set_state  → overlay on every monitor (taskbar hidden, keyboard hook, topmost enforcer)
///   unlock     → overlay off, tray only
///   tray_state → tooltip
///   exit       → quit (service stopping)
/// Without the watchdog it quits: on a failed connect at once, after a lost pipe within 30 s
/// (a restarted watchdog kills it anyway and spawns a fresh one).
/// --standalone-test shows the overlay without a watchdog, for UI work (only from the command line: the
/// watchdog starts vigil_ui without it, and nothing in the environment switches it on, D24);
/// there --mode=night|limit|manual|pause|server_unreachable, --lang=en|pl (instead of the Windows language) and
/// --exit-after=SECONDS (closes itself and restores the taskbar; a killed process cannot).
/// </summary>
[SupportedOSPlatform("windows")]
public partial class App : Application
{
    /// <summary>Started with --standalone-test (UI work without a watchdog).</summary>
    public static bool IsStandalone { get; } = Environment.GetCommandLineArgs().Contains("--standalone-test");

    private static readonly TimeSpan PipeLossExit = TimeSpan.FromSeconds(30);
    // The grant message lives on the overlay, which goes as soon as the watchdog sends unlock (its next
    // heartbeat, 0-5 s later): keep it readable. Only the unlock waits, so fail-closed is unaffected.
    private static readonly TimeSpan GrantMessageMin = TimeSpan.FromSeconds(4);

    public LockViewModel ViewModel { get; } = new();
    public NamedPipeIpcClient? IpcClient { get; private set; }

    private readonly List<LockWindow> _windows = new();
    private readonly TopmostEnforcer _topmostEnforcer = new();
    private KeyboardBlocker? _kbBlocker;
    private bool _useKeyboardBlocker = true;
    private DispatcherTimer? _clockTimer;
    private DispatcherTimer? _pipeLossTimer;
    private DispatcherTimer? _deferredUnlock;
    private TrayIcon? _tray;
    private bool _locked;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // ui.log is the only trace of this process: a crash or an exit with the session has to land there too.
        UiLog.Write($"start pid={Environment.ProcessId}");
        DispatcherUnhandledException += (_, ex) => UiLog.Write("unhandled: " + ex.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, ex) => UiLog.Write("unhandled: " + ex.ExceptionObject);
        SessionEnding += (_, ex) => UiLog.Write("session ending: " + ex.ReasonSessionEnding);

        // A previous instance killed while locked leaves the taskbar hidden.
        TaskbarHider.RestoreAll();

        _useKeyboardBlocker = !Array.Exists(e.Args, a => a == "--no-keyboard-blocker");
        SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
        _clockTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        _clockTimer.Tick += (_, _) => ViewModel.CurrentTime = DateTime.Now.ToString("HH:mm");
        _clockTimer.Start();

        if (IsStandalone)
        {
            ApplyArgvStandalone(e.Args);
            return;
        }

        // Before connecting: the first tray_state may arrive right after the connect.
        _tray = new TrayIcon(OnTrayClick);
        try
        {
            IpcClient = new NamedPipeIpcClient();
            IpcClient.OnMessage += m => Dispatcher.InvokeAsync(() => HandleIpcMessage(m));
            IpcClient.OnDisconnected += () => Dispatcher.InvokeAsync(OnPipeLost);
            await IpcClient.ConnectAsync(timeoutMs: 5000, CancellationToken.None);
        }
        catch (Exception ex)
        {
            // Only the watchdog may run us: it creates the pipe right before spawning.
            UiLog.Write("pipe connect failed, exiting: " + ex.Message);
            Shutdown(1);
            return;
        }

        UiLog.Write("connected to the watchdog");
    }

    private void HandleIpcMessage(object message)
    {
        switch (message)
        {
            case IpcSetState s:
                ShowLock(s);
                break;
            case IpcUnlock:
                Unlock();
                break;
            case IpcTrayState t:
                _tray?.ShowState(t);
                break;
            case IpcExit:
                Shutdown(0);
                break;
        }
    }

    private void ShowLock(IpcSetState s)
    {
        // set_state comes with every locked heartbeat. After unlock (deferred, see Unlock) it is a real new
        // lock; before it, within GrantMessageMin of the grant, it is the reply to a heartbeat sent before
        // the grant and must not take the grant message away.
        var relockAfterUnlock = _deferredUnlock is not null;
        _deferredUnlock?.Stop();
        _deferredUnlock = null;
        LockReason reason;
        try { reason = LockReasonExtensions.FromWireFormat(s.LockReason); }
        catch (ArgumentOutOfRangeException) { reason = LockReason.Limit; }

        if (!_locked)
        {
            // A fresh lock starts clean: no code or message left over from the previous one.
            ViewModel.Phase = LockUiPhase.Locked;
            ViewModel.TotpCode = "";
            ViewModel.StatusMessage = "";
        }
        else if (ViewModel.Phase == LockUiPhase.GrantApplied
                 && (relockAfterUnlock || DateTime.UtcNow - ViewModel.GrantAppliedAtUtc >= GrantMessageMin))
        {
            // Still locked after a grant (e.g. another lock reason applies): back to the code field.
            ViewModel.Phase = LockUiPhase.Locked;
        }

        ViewModel.State = new UiState
        {
            Reason = reason,
            ShowTotpField = s.ShowTotpField,
            ShowResumeButton = s.ShowResumeButton,
            ShowRetryButton = s.ShowRetryButton,
            MinutesRemaining = s.MinutesRemaining,
        };
        if (!string.IsNullOrEmpty(s.CurrentTimeDisplay))
            ViewModel.CurrentTime = s.CurrentTimeDisplay;

        if (_locked) return;
        _locked = true;
        UiLog.Write("lock: " + s.LockReason);
        if (_useKeyboardBlocker)
        {
            _kbBlocker ??= new KeyboardBlocker();
            try { _kbBlocker.Install(); }
            catch (Exception ex) { UiLog.Write("keyboard hook failed: " + ex.Message); }
        }
        CreateWindowsForAllScreens();
    }

    private void Unlock()
    {
        var left = GrantMessageMin - (DateTime.UtcNow - ViewModel.GrantAppliedAtUtc);
        if (ViewModel.Phase != LockUiPhase.GrantApplied || left <= TimeSpan.Zero)
        {
            HideLock();
            return;
        }
        if (_deferredUnlock is not null) return;  // already waiting
        _deferredUnlock = new DispatcherTimer { Interval = left };
        _deferredUnlock.Tick += (_, _) => HideLock();
        _deferredUnlock.Start();
    }

    private void HideLock()
    {
        _deferredUnlock?.Stop();
        _deferredUnlock = null;
        if (!_locked) return;
        _locked = false;
        UiLog.Write("unlock");
        CloseWindows();
        _kbBlocker?.Uninstall();
        // The taskbar windows that exist now, not the ones seen at lock time: after a boot into a
        // lock Explorer creates them later, TopmostEnforcer hides those too, and restoring the old
        // handles left the taskbar hidden after unlock.
        TaskbarHider.RestoreAll();
    }

    private void OnPipeLost()
    {
        UiLog.Write("pipe to the watchdog lost");
        _tray?.ShowDisconnected();
        ViewModel.StatusMessage = Strings.WatchdogLost;
        // The overlay stays meanwhile (fail-closed); a restarted watchdog replaces this process.
        _pipeLossTimer = new DispatcherTimer { Interval = PipeLossExit };
        _pipeLossTimer.Tick += (_, _) => Shutdown(0);
        _pipeLossTimer.Start();
    }

    private async void OnTrayClick()
    {
        var ipc = IpcClient;
        if (ipc is null || !ipc.IsConnected) return;
        try
        {
            await ipc.SendAsync(new IpcPauseToggle(), CancellationToken.None);
            UiLog.Write("pause click sent");
        }
        catch (Exception ex)
        {
            UiLog.Write("pause click failed: " + ex.Message);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _clockTimer?.Stop();
        _pipeLossTimer?.Stop();
        HideLock();
        _topmostEnforcer.Dispose();
        SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
        _kbBlocker?.Dispose();
        _tray?.Dispose();
        IpcClient?.Dispose();
        base.OnExit(e);
    }

    private void OnDisplaySettingsChanged(object? sender, EventArgs e)
    {
        // Monitor plugged/unplugged or DPI changed: one window per screen again.
        if (!_locked) return;
        CloseWindows();
        CreateWindowsForAllScreens();
    }

    private void CloseWindows()
    {
        _topmostEnforcer.Clear();
        foreach (var w in _windows) w.Close();
        _windows.Clear();
    }

    private void CreateWindowsForAllScreens()
    {
        // Hide the taskbar while locked: otherwise Start could launch something over the overlay.
        // TopmostEnforcer hides it again every 500 ms (Explorer may recreate it).
        TaskbarHider.Hide();

        foreach (var screen in WinFormsScreen.AllScreens)
        {
            var window = new LockWindow(ViewModel, screen);
            _windows.Add(window);
            window.Show();
            window.Activate();  // keyboard input goes to the TOTP field
            _topmostEnforcer.Track(window);
        }
        // Every 500 ms back on top, in case something covers us.
        _topmostEnforcer.Start();
    }

    /// <summary>Standalone test mode: local UI work without a watchdog; never used when deployed.</summary>
    private void ApplyArgvStandalone(string[] args)
    {
        var reason = "limit";
        var exitAfter = 0;
        foreach (var a in args)
        {
            if (a.StartsWith("--exit-after=", StringComparison.Ordinal)) int.TryParse(a["--exit-after=".Length..], out exitAfter);
            else if (a.StartsWith("--mode=", StringComparison.Ordinal)) reason = a["--mode=".Length..];
            // Before the first use of Strings, which reads the culture once.
            else if (a.StartsWith("--lang=", StringComparison.Ordinal))
                System.Globalization.CultureInfo.CurrentUICulture = new System.Globalization.CultureInfo(a["--lang=".Length..]);
        }
        ShowLock(new IpcSetState
        {
            LockReason = reason,
            ShowTotpField = true,
            ShowRetryButton = reason == "server_unreachable",
        });
        if (exitAfter > 0)
        {
            var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(exitAfter) };
            timer.Tick += (_, _) => { timer.Stop(); HideLock(); Shutdown(0); };
            timer.Start();
        }
    }
}
