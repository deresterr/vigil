using System.Runtime.Versioning;
using System.Windows;
using System.Windows.Input;
using Vigil.Lockscreen.Native;

namespace Vigil.Lockscreen;

[SupportedOSPlatform("windows")]
public partial class EmergencyWindow : Window
{
    /// <summary>True jeśli password był poprawny i flag zapisany.</summary>
    public bool Unlocked { get; private set; }

    public EmergencyWindow()
    {
        InitializeComponent();
        Loaded += (_, _) => PasswordInput.Focus();
    }

    private void Unlock_Click(object sender, RoutedEventArgs e)
    {
        TryUnlock();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        Unlocked = false;
        Close();
    }

    private void PasswordInput_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter || e.Key == Key.Return)
        {
            TryUnlock();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            Unlocked = false;
            Close();
            e.Handled = true;
        }
    }

    private void TryUnlock()
    {
        if (!EmergencyAuth.IsConfigured())
        {
            StatusText.Text = "Hasło awaryjne nie jest skonfigurowane.";
            return;
        }

        var pwd = PasswordInput.Password;
        if (string.IsNullOrEmpty(pwd))
        {
            StatusText.Text = "Wpisz hasło.";
            return;
        }

        if (EmergencyAuth.TryVerify(pwd))
        {
            try
            {
                EmergencyAuth.WriteBypassFlag(validHours: 24);
                Unlocked = true;
                Close();
            }
            catch (System.Exception ex)
            {
                StatusText.Text = "Błąd zapisu flagi: " + ex.Message;
            }
        }
        else
        {
            StatusText.Text = "Hasło niepoprawne.";
            PasswordInput.Clear();
            PasswordInput.Focus();
        }
    }
}
