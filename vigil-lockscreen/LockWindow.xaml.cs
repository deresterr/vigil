using System;
using System.Diagnostics;
using System.Linq;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using Vigil.Lockscreen.Native;
using Vigil.Lockscreen.ViewModels;
// Alias dla Screen z WinForms — żeby nie wciągać całego namespace, który konfliktuje z WPF
// (np. KeyEventArgs jest w obu, Application też).
using WinFormsScreen = System.Windows.Forms.Screen;
// MessageBox z WPF (System.Windows.MessageBox) — alias żeby krócej się odwoływać.
using MessageBox = System.Windows.MessageBox;

namespace Vigil.Lockscreen;

/// <summary>
/// Fullscreen lockscreen window. Każdy monitor dostaje swoją instancję — wszystkie
/// współdzielą ten sam ViewModel.
///
/// Click handlers w code-behind (a nie commands) są celowym wyborem — kod jest mały,
/// a commands wymagałyby relay-command boilerplate. WPF idiom MVVM jest tu nadmiarowy.
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

        // Pozycjonowanie na właściwym ekranie zanim WindowState=Maximized
        // (Maximized zachowuje się na ekranie gdzie jest okno gdy go ustawiamy).
        var bounds = screen.Bounds;
        Left = bounds.Left;
        Top = bounds.Top;
        Width = bounds.Width;
        Height = bounds.Height;

        // Block Alt+F4 (jest też zablokowany przez KeyboardBlocker, ale defense in depth).
        PreviewKeyDown += OnPreviewKeyDown;
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        // Awaryjne odblokowanie: Ctrl+Shift+Alt+E → modal z password promptem.
        // KeyboardBlocker globalny hook nie łapie tej kombinacji (tylko Win/Alt+Tab/F4/Esc,
        // Ctrl+Esc, F1/F11/F12), więc trafi tu w WPF.
        if (e.Key == Key.E
            && (Keyboard.Modifiers & ModifierKeys.Control) != 0
            && (Keyboard.Modifiers & ModifierKeys.Shift) != 0
            && (Keyboard.Modifiers & ModifierKeys.Alt) != 0)
        {
            e.Handled = true;
            OpenEmergencyDialog();
            return;
        }

        // Alt+F4
        if (e.Key == Key.System && e.SystemKey == Key.F4)
        {
            e.Handled = true;
            return;
        }
        // Escape — nie zamyka window (tylko może wyczyścić TOTP input)
        if (e.Key == Key.Escape)
        {
            if (_vm.Phase == LockUiPhase.Locked)
            {
                _vm.TotpCode = "";
            }
            e.Handled = true;
        }
    }

    private void OpenEmergencyDialog()
    {
        // EmergencyAuth.IsConfigured() jest też sprawdzane wewnątrz dialogu — tu tylko
        // żeby uniknąć flash okna gdy nie ma czego pytać.
        if (!EmergencyAuth.IsConfigured())
        {
            return;
        }

        // Pauzujemy TopmostEnforcer: bez tego co 500 ms wracalibyśmy z fokusem na LockWindow,
        // co odbiera focus z PasswordBox i uniemożliwia rodzicowi wpisanie hasła.
        // KeyboardBlocker pozostaje aktywny → nawet z pauzą Win/Alt+Tab/F4 są blokowane.
        // Cast bezpieczny przez `as` — jeśli kiedyś ktoś podmieni App, Pause/Resume są no-op.
        var app = System.Windows.Application.Current as App;
        app?.PauseTopmostEnforcer();
        bool unlocked;
        try
        {
            var dlg = new EmergencyWindow { Owner = this };
            dlg.ShowDialog();
            unlocked = dlg.Unlocked;
        }
        finally
        {
            // Wznawiamy enforcer-a niezależnie od wyniku. Przy Unlocked=true zaraz
            // Application.Shutdown sprzątnie wszystko, ale Resume i tak jest tanie.
            app?.ResumeTopmostEnforcer();
        }

        if (unlocked)
        {
            // Flag plik zapisany. Zamykamy lockscreen — watchdog widzi flagę i nie spawnuje
            // przez kolejne 24h. ShutdownMode=OnExplicitShutdown więc trzeba Application.Current.Shutdown.
            System.Windows.Application.Current.Shutdown(0);
        }
    }

    // === Locked phase handlers ===

    private void TotpInput_KeyDown(object sender, KeyEventArgs e)
    {
        // Enter w TOTP field = klik "Sprawdź"
        if (e.Key == Key.Enter || e.Key == Key.Return)
        {
            CheckTotp_Click(sender, e);
            e.Handled = true;
        }
    }

    private async void CheckTotp_Click(object sender, RoutedEventArgs e)
    {
        var code = _vm.TotpCode.Trim();
        if (code.Length != 6 || !code.All(char.IsDigit))
        {
            _vm.StatusMessage = "Kod musi mieć 6 cyfr.";
            return;
        }

        // Hardcoded 60 min grant — uproszczenie UX. Dziecko podaje sam kod, dostaje godzinę.
        // Jeśli rodzic chce inny zakres, używa admin UI (manual grant).
        _vm.StatusMessage = "";
        await ApplyGrant(60);
    }

    private async void Retry_Click(object sender, RoutedEventArgs e)
    {
        _vm.StatusMessage = "Ponawianie połączenia...";
        if (IsStandaloneTestMode()) return;

        var ipc = ((App)System.Windows.Application.Current).IpcClient;
        if (ipc is null || !ipc.IsConnected) return;

        try
        {
            await ipc.SendAsync(new IpcRetryNow(), CancellationToken.None);
        }
        catch (Exception ex)
        {
            _vm.StatusMessage = $"Błąd: {ex.Message}";
        }
    }

    private async void ResumeFromPause_Click(object sender, RoutedEventArgs e)
    {
        // Informujemy watchdoga — on woła serwer (/v1/pause_toggle), serwer wyłącza pauzę.
        // Lockscreen zniknie przy następnym heartbeat gdy lock_reason != "pause".
        if (IsStandaloneTestMode())
        {
            _vm.StatusMessage = "Pauza zakończona (test).";
            return;
        }

        var ipc = ((App)System.Windows.Application.Current).IpcClient;
        if (ipc is null || !ipc.IsConnected) return;

        try
        {
            await ipc.SendAsync(new IpcResumeFromPause(), CancellationToken.None);
            _vm.StatusMessage = "Chwileczkę...";
        }
        catch (Exception ex)
        {
            _vm.StatusMessage = $"Błąd: {ex.Message}";
        }
    }

    private async Task ApplyGrant(int minutes)
    {
        _vm.Phase = LockUiPhase.GrantPending;

        // Standalone test: simulate success
        if (IsStandaloneTestMode())
        {
            await Task.Delay(500);
            _vm.Phase = LockUiPhase.GrantApplied;
            _vm.StatusMessage = $"Dodano {minutes} minut.";
            return;
        }

        // SendTotpAsync robi request/response correlation w pipe client.
        var ipc = ((App)System.Windows.Application.Current).IpcClient;
        if (ipc is null || !ipc.IsConnected)
        {
            _vm.Phase = LockUiPhase.Locked;
            _vm.StatusMessage = "Brak połączenia z watchdogiem.";
            return;
        }

        try
        {
            var resp = await ipc.SendTotpAsync(_vm.TotpCode, minutes, CancellationToken.None);

            if (resp.Ok)
            {
                _vm.Phase = LockUiPhase.GrantApplied;
                _vm.StatusMessage = resp.Applied ?? $"Dodano {minutes} minut.";
                // Lockscreen zostaje na ekranie aż watchdog wyśle IpcExit (zauważy unlock przy
                // następnym tickcie heartbeat).
            }
            else
            {
                // Wracamy do Locked, czyścimy TOTP, pokazujemy błąd
                _vm.Phase = LockUiPhase.Locked;
                _vm.TotpCode = "";
                _vm.StatusMessage = resp.Reason switch
                {
                    "invalid_code" => "Nieprawidłowy kod. Spróbuj ponownie.",
                    "lockout" => $"Zbyt wiele prób. Poczekaj {resp.LockoutSecondsRemaining ?? 0}s.",
                    _ => $"Błąd: {resp.Reason ?? "nieznany"}",
                };
            }
        }
        catch (TimeoutException)
        {
            _vm.Phase = LockUiPhase.Locked;
            _vm.StatusMessage = "Timeout — watchdog nie odpowiedział.";
        }
        catch (Exception ex)
        {
            _vm.Phase = LockUiPhase.Locked;
            _vm.StatusMessage = $"Błąd komunikacji: {ex.Message}";
        }
    }

    // === Shutdown ===

    private void Shutdown_Click(object sender, RoutedEventArgs e)
    {
        var result = MessageBox.Show(
            "Czy na pewno chcesz wyłączyć komputer?",
            "Wyłączanie",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question,
            MessageBoxResult.No);

        if (result != MessageBoxResult.Yes) return;

        try
        {
            // shutdown /s /t 0 — natychmiastowy shutdown
            // Uruchomimy bez user interakcji (zaplanowany shutdown).
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
            _vm.StatusMessage = $"Nie udało się wyłączyć: {ex.Message}";
        }
    }

    // === Helpers ===

    private static bool IsStandaloneTestMode()
    {
        // Standalone fallback dla local UI debug — argv lub env var.
        return Environment.GetCommandLineArgs().Any(a => a == "--standalone-test")
            || Environment.GetEnvironmentVariable("VIGIL_LOCKSCREEN_STANDALONE") == "1";
    }
}
