using System;
using System.Diagnostics;
using System.Linq;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using Vigil.Ui.ViewModels;
// WinForms types by alias: the namespace clashes with WPF (KeyEventArgs, Application).
using WinFormsScreen = System.Windows.Forms.Screen;
using MessageBox = System.Windows.MessageBox;

namespace Vigil.Ui;

/// <summary>
/// Full-screen lock window, one per monitor, all sharing one ViewModel. Click handlers in code-behind on
/// purpose: commands would only add boilerplate here.
/// </summary>
[SupportedOSPlatform("windows")]
public partial class LockWindow : Window
{
    private readonly LockViewModel _vm;
    private readonly WinFormsScreen _screen;

    public LockWindow(LockViewModel vm, WinFormsScreen screen)
    {
        _vm = vm;
        _screen = screen;
        DataContext = vm;
        InitializeComponent();
        DataObject.AddPastingHandler(TotpInput, TotpInput_Pasting);

        // On its monitor before Maximized: a window maximizes on the screen it is on.
        var bounds = screen.Bounds;
        Left = bounds.Left;
        Top = bounds.Top;
        Width = bounds.Width;
        Height = bounds.Height;

        // Block Alt+F4 (KeyboardBlocker does too).
        PreviewKeyDown += OnPreviewKeyDown;
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        // Alt+F4
        if (e.Key == Key.System && e.SystemKey == Key.F4)
        {
            e.Handled = true;
            return;
        }
        // Escape never closes the window (at most it clears the code field)
        if (e.Key == Key.Escape)
        {
            if (_vm.Phase == LockUiPhase.Locked)
            {
                _vm.TotpCode = "";
            }
            e.Handled = true;
        }
    }

    // === Locked phase handlers ===

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
        _vm.StatusMessage = Strings.Reconnecting;
        if (App.IsStandalone) return;

        var ipc = ((App)System.Windows.Application.Current).IpcClient;
        if (ipc is null || !ipc.IsConnected) return;

        try
        {
            await ipc.SendAsync(new IpcRetryNow(), CancellationToken.None);
        }
        catch (Exception ex)
        {
            _vm.StatusMessage = Strings.Error(ex.Message);
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
        if (ipc is null || !ipc.IsConnected) return;

        try
        {
            await ipc.SendAsync(new IpcResumeFromPause(), CancellationToken.None);
            _vm.StatusMessage = Strings.OneMoment;
        }
        catch (Exception ex)
        {
            _vm.StatusMessage = Strings.Error(ex.Message);
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
            _vm.StatusMessage = Strings.NoWatchdog;
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
                _vm.Phase = LockUiPhase.Locked;
                _vm.TotpCode = "";
                _vm.StatusMessage = Strings.CodeRejected(resp.Reason, resp.LockoutSecondsRemaining);
            }
        }
        catch (TimeoutException)
        {
            _vm.Phase = LockUiPhase.Locked;
            _vm.StatusMessage = Strings.WatchdogTimeout;
        }
        catch (Exception ex)
        {
            _vm.Phase = LockUiPhase.Locked;
            _vm.StatusMessage = Strings.Error(ex.Message);
        }
    }

    // === Shutdown ===

    private void Shutdown_Click(object sender, RoutedEventArgs e)
    {
        var result = MessageBox.Show(
            Strings.ShutDownConfirm,
            Strings.ShutDownTitle,
            MessageBoxButton.YesNo,
            MessageBoxImage.Question,
            MessageBoxResult.No);

        if (result != MessageBoxResult.Yes) return;

        try
        {
            // shutdown /s /t 0: shut down now
            Process.Start(new ProcessStartInfo
            {
                FileName = "shutdown.exe",
                Arguments = "/s /t 0",
                UseShellExecute = false,
                CreateNoWindow = true,
            });
        }
        catch (Exception ex)
        {
            _vm.StatusMessage = Strings.ShutDownFailed(ex.Message);
        }
    }
}
