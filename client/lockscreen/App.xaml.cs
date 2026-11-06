using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Vigil.Lockscreen.Native;
using Vigil.Lockscreen.ViewModels;
using Microsoft.Win32;
// Screen z System.Windows.Forms wchodzi w konflikt z System.Windows.Application (WPF) i innymi
// typami. Alias dla samego Screen — nie wciągamy całego namespace.
using WinFormsScreen = System.Windows.Forms.Screen;

namespace Vigil.Lockscreen;

/// <summary>
/// Entry point WPF lockscreen. Główne zadania:
/// - Stworzyć fullscreen LockWindow per każdy ekran
/// - Reagować na DisplaySettingsChanged (dołączenie/odłączenie monitora)
/// - Trzymać centralny ViewModel — wszystkie windows pokazują tę samą informację
/// - Aktualizować zegar co minutę
/// - Połączyć IPC z vigil_watchdog; standalone fallback przez --standalone-test
///   lub VIGIL_LOCKSCREEN_STANDALONE=1 (lokalny debug UI bez watchdoga).
/// </summary>
[SupportedOSPlatform("windows")]
public partial class App : Application
{
    public LockViewModel ViewModel { get; } = new();
    public NamedPipeIpcClient? IpcClient { get; private set; }

    private readonly List<LockWindow> _windows = new();
    private KeyboardBlocker? _kbBlocker;
    private System.Windows.Threading.DispatcherTimer? _clockTimer;
    private System.Windows.Threading.DispatcherTimer? _brokenTimer;
    private DateTime? _pipeBrokenAt;
    private IntPtr[]? _hiddenTaskbars;
    private readonly TopmostEnforcer _topmostEnforcer = new();
    private bool _isStandalone;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Tryb pomocniczy watchdoga: przywróć pasek zadań i natychmiast wyjdź.
        // Spawnowany przez RealLockscreenManager.DismissAsync() po zakończeniu głównego
        // lockscreen (zarówno graceful jak i Kill) — gwarantuje restore nawet gdy
        // normalny OnExit() nie zdążył się wykonać przed zabiciem procesu.
        // Nie inicjalizujemy nic z lockscreen (brak IPC, okien, hooków klawiaturowych).
        if (Array.Exists(e.Args, a => a == "--restore-taskbar-only"))
        {
            TaskbarHider.RestoreAll();
            Shutdown(0);
            return;
        }

        _isStandalone = Array.Exists(e.Args, a => a == "--standalone-test")
            || Environment.GetEnvironmentVariable("VIGIL_LOCKSCREEN_STANDALONE") == "1";

        if (_isStandalone)
        {
            ApplyArgvStandalone(e.Args);
        }
        else
        {
            // Failed connect = nie ma watchdoga → fail-fast (Shutdown).
            // Vigil watchdog jest jedynym legitnym launcherem lockscreen.
            try
            {
                IpcClient = new NamedPipeIpcClient();
                IpcClient.OnMessage += HandleIpcMessage;
                IpcClient.OnDisconnected += () =>
                {
                    Dispatcher.InvokeAsync(() =>
                    {
                        ViewModel.StatusMessage = "Utracono połączenie z watchdogiem.";
                        // Auto-shutdown po 30s bez pipe — gdy vigil_watchdog crashował i się
                        // restartuje, nowy watchdog spawnuje świeży lockscreen. Stary musi się
                        // sam zamknąć żeby uniknąć duplikatów na ekranie.
                        // Jeśli pipe wróci szybciej (np. transient race), zamykanie się anuluje
                        // przez kolejne IpcSetState message (poniżej w HandleIpcMessage).
                        _pipeBrokenAt = DateTime.UtcNow;
                        if (_brokenTimer is null)
                        {
                            _brokenTimer = new System.Windows.Threading.DispatcherTimer
                            {
                                Interval = TimeSpan.FromSeconds(2),
                            };
                            _brokenTimer.Tick += (_, _) =>
                            {
                                if (_pipeBrokenAt is { } t && (DateTime.UtcNow - t).TotalSeconds >= 30)
                                {
                                    _brokenTimer!.Stop();
                                    Shutdown(0);
                                }
                            };
                            _brokenTimer.Start();
                        }
                    });
                };
                await IpcClient.ConnectAsync(timeoutMs: 5000, CancellationToken.None);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"IPC connect failed: {ex.Message}");
                // Fail-fast: jeśli nie podpinamy się do pipe, nie chcemy zostać sami na ekranie.
                // Vigil watchdog zauważy że nie ma pipe i ponownie spawnuje.
                Shutdown(1);
                return;
            }
        }

        // Keyboard blocker (system-wide low-level hook). Można wyłączyć dla debug.
        if (!Array.Exists(e.Args, a => a == "--no-keyboard-blocker"))
        {
            _kbBlocker = new KeyboardBlocker();
            try
            {
                _kbBlocker.Install();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"KeyboardBlocker install failed: {ex.Message}");
            }
        }

        // Multi-monitor windows
        CreateWindowsForAllScreens();

        // Reaguj na zmianę monitorów (HDMI plug/unplug, DPI scaling change)
        SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;

        // Zegar — update co 30s wystarczy (display tylko HH:MM)
        _clockTimer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(30),
        };
        _clockTimer.Tick += (_, _) => ViewModel.CurrentTime = DateTime.Now.ToString("HH:mm");
        _clockTimer.Start();
    }

    /// <summary>
    /// Dispatch incoming IPC messages do ViewModel. Wywoływane z reader thread → marshal do UI.
    /// </summary>
    private void HandleIpcMessage(object message)
    {
        Dispatcher.InvokeAsync(() =>
        {
            switch (message)
            {
                case IpcSetState s:
                    // Pipe activity — anuluj broken-pipe shutdown timer jeśli był aktywny.
                    _pipeBrokenAt = null;
                    if (_brokenTimer is not null) { _brokenTimer.Stop(); _brokenTimer = null; }

                    LockReason reason;
                    try
                    {
                        reason = LockReasonExtensions.FromWireFormat(s.LockReason);
                    }
                    catch
                    {
                        reason = LockReason.Limit;  // safe fallback
                    }
                    ViewModel.State = new UiState
                    {
                        Reason = reason,
                        MainMessage = s.MainMessage,
                        ShowTotpField = s.ShowTotpField,
                        ShowResumeButton = s.ShowResumeButton,
                        ShowRetryButton = s.ShowRetryButton,
                        ShowShutdownButton = s.ShowShutdownButton,
                        MinutesRemaining = s.MinutesRemaining,
                    };
                    if (!string.IsNullOrEmpty(s.CurrentTimeDisplay))
                    {
                        ViewModel.CurrentTime = s.CurrentTimeDisplay;
                    }
                    // Jeśli wcześniej byliśmy w GrantApplied phase i przyszedł nowy set_state
                    // (rzadko — watchdog normalnie wysyła exit po grancie), wracamy do Locked.
                    if (ViewModel.Phase == LockUiPhase.GrantApplied)
                    {
                        ViewModel.Phase = LockUiPhase.Locked;
                    }
                    break;

                case IpcExit:
                    Shutdown(0);
                    break;
            }
        });
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _clockTimer?.Stop();
        _topmostEnforcer.Dispose();
        if (_hiddenTaskbars is not null)
        {
            TaskbarHider.Restore(_hiddenTaskbars);
            _hiddenTaskbars = null;
        }
        SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
        _kbBlocker?.Dispose();
        IpcClient?.Dispose();
        base.OnExit(e);
    }

    private void OnDisplaySettingsChanged(object? sender, EventArgs e)
    {
        // Zniszcz wszystkie aktualne windows i utwórz na nowo per Screen.AllScreens
        foreach (var w in _windows.ToList())
        {
            w.Close();
        }
        _windows.Clear();
        CreateWindowsForAllScreens();
    }

    private void CreateWindowsForAllScreens()
    {
        // 1. Ukryj pasek zadań Windows na czas trwania lockscreena.
        //    Bez tego dziecko mogłoby kliknąć Start i uruchomić program który przykryłby
        //    lockscreen wizualnie (keyboard hook by działał, ale UI by była nieczynna).
        if (_hiddenTaskbars is null)
        {
            _hiddenTaskbars = TaskbarHider.Hide();
        }

        // 2. Stwórz windows
        foreach (var screen in WinFormsScreen.AllScreens)
        {
            var window = new LockWindow(ViewModel, screen);
            _windows.Add(window);
            window.Show();
            window.Activate();  // Force focus, żeby keyboard input szedł do TOTP TextBox
            _topmostEnforcer.Track(window);
        }

        // 3. Co 500 ms reasserts topmost — defense in depth gdyby coś próbowało nas przykryć.
        _topmostEnforcer.Start();
    }

    /// <summary>
    /// Standalone test mode — local UI debug bez watchdoga. W deployment zawsze IPC.
    /// </summary>
    private void ApplyArgvStandalone(string[] args)
    {
        var reason = LockReason.Limit;
        string? message = null;

        foreach (var a in args)
        {
            if (a.StartsWith("--mode=", StringComparison.Ordinal))
            {
                var modeStr = a["--mode=".Length..];
                try { reason = LockReasonExtensions.FromWireFormat(modeStr); }
                catch { /* default */ }
            }
            else if (a.StartsWith("--message=", StringComparison.Ordinal))
            {
                message = a["--message=".Length..];
            }
        }

        // Initial state only (from command-line args); the watchdog sends the real one over IPC,
        // including whether the TOTP field makes sense offline (#9).
        var showRetry = reason == LockReason.ServerUnreachable;
        var showTotp = true;

        ViewModel.State = new UiState
        {
            Reason = reason,
            MainMessage = message,
            ShowTotpField = showTotp,
            ShowRetryButton = showRetry,
            ShowShutdownButton = true,
        };
    }
}
