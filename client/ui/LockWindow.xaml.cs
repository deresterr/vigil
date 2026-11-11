using System;
using System.ComponentModel;
using System.Linq;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using Vigil.Ui.Native;
using Vigil.Ui.ViewModels;
// WinForms types by alias: the namespace clashes with WPF (KeyEventArgs, Application).
using WinFormsScreen = System.Windows.Forms.Screen;

namespace Vigil.Ui;

/// <summary>
/// Full-screen lock window, one per monitor, all sharing one ViewModel. Click handlers in code-behind on
/// purpose: commands would only add boilerplate here.
///
/// It cannot be closed, moved or minimized from inside (#120): no system menu (Alt+Space), system commands
/// refused, Closing cancelled unless App closes it. TopmostEnforcer puts it back when another process moves it.
/// </summary>
[SupportedOSPlatform("windows")]
public partial class LockWindow : Window
{
    private readonly LockViewModel _vm;
    private bool _closeAllowed;

    /// <summary>The monitor this window covers, in pixels (Screen.Bounds): placement goes through Win32, never
    /// WPF's Left/Width, which are device-independent units.</summary>
    public System.Drawing.Rectangle ScreenBounds { get; }

    public LockWindow(LockViewModel vm, WinFormsScreen screen)
    {
        _vm = vm;
        ScreenBounds = screen.Bounds;
        DataContext = vm;
        InitializeComponent();
        DataObject.AddPastingHandler(TotpInput, TotpInput_Pasting);
        PreviewKeyDown += OnPreviewKeyDown;
        SourceInitialized += OnSourceInitialized;
        Closing += OnClosing;
    }

    /// <summary>App unlocking or rebuilding the windows: the only close that goes through.</summary>
    public void CloseFromApp()
    {
        _closeAllowed = true;
        Close();
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_closeAllowed) return;
        e.Cancel = true;  // WM_CLOSE from anywhere else (another process can post it)
        UiLog.Write("lock window close refused");
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        // Before the window is first shown: its place on its monitor, in pixels, and no system menu.
        var hwnd = new WindowInteropHelper(this).Handle;
        WindowFrame.RemoveSystemMenu(hwnd);
        WindowFrame.Place(hwnd, ScreenBounds);
        HwndSource.FromHwnd(hwnd)?.AddHook(WndProc);
    }

    private static IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WindowFrame.WM_SYSCOMMAND && WindowFrame.IsBlockedSysCommand(wParam))
            handled = true;
        return IntPtr.Zero;
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        // Alt+F4 and Alt+Space (the system menu), also swallowed by KeyboardBlocker
        if (e.Key == Key.System && e.SystemKey is Key.F4 or Key.Space)
        {
            e.Handled = true;
            return;
        }
        // Escape never closes the window: it clears the code field or cancels the shutdown question
        if (e.Key == Key.Escape)
        {
            if (_vm.Phase == LockUiPhase.Locked) _vm.TotpCode = "";
            else if (_vm.Phase == LockUiPhase.ConfirmShutdown) _vm.Phase = LockUiPhase.Locked;
            e.Handled = true;
        }
    }

    // === TOTP field: digits only, and it keeps the keyboard focus ===

    private void TotpInput_PreviewTextInput(object sender, TextCompositionEventArgs e)
    {
        e.Handled = !e.Text.All(char.IsAsciiDigit);
    }

    private void TotpInput_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        // Space does not raise PreviewTextInput in a WPF TextBox.
        if (e.Key == Key.Space) e.Handled = true;
    }

    private void TotpInput_Pasting(object sender, DataObjectPastingEventArgs e)
    {
        // Accept "123 456" or "123-456" from a password manager: keep the digits, max 6.
        e.CancelCommand();
        if (e.SourceDataObject.GetData(DataFormats.UnicodeText) is not string pasted) return;
        var box = TotpInput;
        var merged = box.Text[..box.SelectionStart] + pasted + box.Text[(box.SelectionStart + box.SelectionLength)..];
        var digits = new string(merged.Where(char.IsAsciiDigit).Take(6).ToArray());
        box.Text = digits;
        box.CaretIndex = digits.Length;
    }

    private void TotpInput_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        // The field is hidden while a code is being checked and the keyboard focus goes with it;
        // without this, digits typed after a wrong code went nowhere until the field was clicked.
        if (!TotpInput.IsVisible) return;
        Dispatcher.BeginInvoke(() =>
        {
            // Logical focus in every window; keyboard focus only in the active one, so lock windows
            // on several monitors do not fight over activation. WPF restores the logical focus when a
            // window is activated (e.g. by TopmostEnforcer).
            FocusManager.SetFocusedElement(this, TotpInput);
            if (IsActive) Keyboard.Focus(TotpInput);
        }, System.Windows.Threading.DispatcherPriority.Input);
    }

    private void TotpInput_KeyDown(object sender, KeyEventArgs e)
    {
        // Enter in the code field = the Check button
        if (e.Key == Key.Enter || e.Key == Key.Return)
        {
            CheckTotp_Click(sender, e);
            e.Handled = true;
        }
    }

    private async void CheckTotp_Click(object sender, RoutedEventArgs e)
    {
        var code = _vm.TotpCode.Trim();
        if (code.Length != 6 || !code.All(char.IsAsciiDigit))
        {
            _vm.StatusMessage = Strings.CodeNeedsSixDigits;
            return;
        }

        // Always 60 min: the code alone grants an hour; other amounts from the parent panel.
        _vm.StatusMessage = "";
        await ApplyGrant(60);
    }

    private async void Retry_Click(object sender, RoutedEventArgs e)
    {
        if (App.IsStandalone)
        {
            _vm.StatusMessage = Strings.Reconnecting;
            return;
        }

        var ipc = ((App)System.Windows.Application.Current).IpcClient;
        if (ipc is null || !ipc.IsConnected)
        {
            _vm.StatusMessage = Strings.NoService;
            return;
        }

        try
        {
            await ipc.SendAsync(new IpcRetryNow(), CancellationToken.None);
            _vm.StatusMessage = Strings.Reconnecting;
        }
        catch (Exception ex)
        {
            UiLog.Write("retry failed: " + ex.Message);
            _vm.StatusMessage = Strings.NoService;
        }
    }

    private async void ResumeFromPause_Click(object sender, RoutedEventArgs e)
    {
        // The watchdog asks the server (/v1/pause_toggle) to end the pause.
        // The overlay goes away on the next heartbeat once lock_reason != "pause".
        if (App.IsStandalone)
        {
            _vm.StatusMessage = Strings.PauseEndedTest;
            return;
        }

        var ipc = ((App)System.Windows.Application.Current).IpcClient;
        if (ipc is null || !ipc.IsConnected)
        {
            _vm.StatusMessage = Strings.NoService;
            return;
        }

        try
        {
            await ipc.SendAsync(new IpcResumeFromPause(), CancellationToken.None);
            _vm.StatusMessage = Strings.OneMoment;
        }
        catch (Exception ex)
        {
            UiLog.Write("resume failed: " + ex.Message);
            _vm.StatusMessage = Strings.NoService;
        }
    }

    private async Task ApplyGrant(int minutes)
    {
        _vm.Phase = LockUiPhase.GrantPending;

        // Standalone test: simulate success
        if (App.IsStandalone)
        {
            await Task.Delay(500);
            _vm.Phase = LockUiPhase.GrantApplied;
            _vm.StatusMessage = Strings.Granted("extend_limit", minutes, null);
            return;
        }

        // SendTotpAsync pairs the request with its response in the pipe client.
        var ipc = ((App)System.Windows.Application.Current).IpcClient;
        if (ipc is null || !ipc.IsConnected)
        {
            _vm.Phase = LockUiPhase.Locked;
            _vm.StatusMessage = Strings.NoService;
            return;
        }

        try
        {
            var resp = await ipc.SendTotpAsync(_vm.TotpCode, minutes, CancellationToken.None);

            if (resp.Ok)
            {
                _vm.Phase = LockUiPhase.GrantApplied;
                _vm.StatusMessage = Strings.Granted(resp.Mode, resp.Minutes ?? minutes, resp.UntilLocal);
                // The overlay stays until the watchdog sends IpcUnlock (on its next heartbeat).
            }
            else
            {
                // Back to Locked: clear the code, show the error
                if (resp.Reason is not ("invalid_code" or "lockout" or "offline_not_configured" or "network_error"))
                    UiLog.Write("code rejected: " + resp.Reason);
                _vm.Phase = LockUiPhase.Locked;
                _vm.TotpCode = "";
                _vm.StatusMessage = Strings.CodeRejected(resp.Reason, resp.LockoutSecondsRemaining);
            }
        }
        catch (TimeoutException)
        {
            _vm.Phase = LockUiPhase.Locked;
            _vm.StatusMessage = Strings.ServiceTimeout;
        }
        catch (Exception ex)
        {
            UiLog.Write("code check failed: " + ex.Message);
            _vm.Phase = LockUiPhase.Locked;
            _vm.StatusMessage = Strings.CodeNotChecked;
        }
    }

    // === Shutdown: asked on the overlay (a system message box fought the topmost windows for the focus) ===

    private void Shutdown_Click(object sender, RoutedEventArgs e)
    {
        if (_vm.Phase != LockUiPhase.Locked) return;  // not while a code is being checked or a grant is shown
        _vm.StatusMessage = "";
        _vm.Phase = LockUiPhase.ConfirmShutdown;
        Dispatcher.BeginInvoke(() => ConfirmShutdownButton.Focus(), System.Windows.Threading.DispatcherPriority.Input);
    }

    private void CancelShutdown_Click(object sender, RoutedEventArgs e) => _vm.Phase = LockUiPhase.Locked;

    private void ConfirmShutdown_Click(object sender, RoutedEventArgs e)
    {
        if (App.IsStandalone)
        {
            UiLog.Write("shutdown confirmed (test, not shutting down)");
            _vm.Phase = LockUiPhase.Locked;
            return;
        }
        try
        {
            SystemShutdown.PowerOff();
            UiLog.Write("shutdown started");
        }
        catch (Exception ex)
        {
            UiLog.Write("shutdown failed: " + ex.Message);
            _vm.Phase = LockUiPhase.Locked;
            _vm.StatusMessage = Strings.ShutDownFailed;
        }
    }
}
