using System.Runtime.Versioning;
using Forms = System.Windows.Forms;

namespace Vigil.Ui;

/// <summary>
/// Pause icon in the notification area (was vigil_tray.exe before D8).
///
///   Left click  — toggle the pause (the watchdog asks the server)
///   Hover       — time remaining (Strings.Remaining), refreshed after every heartbeat
///
/// WinForms NotifyIcon works inside the WPF app: its hidden message window is pumped by the WPF
/// dispatcher on the UI thread. All members must be called on that thread.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class TrayIcon : IDisposable
{
    private readonly Forms.NotifyIcon _icon;
    private readonly System.Drawing.Icon _image;

    public TrayIcon(Action onClick)
    {
        _image = IconPainter.TrayIcon();
        _icon = new Forms.NotifyIcon { Icon = _image, Text = Strings.TrayLoading, Visible = true };
        _icon.MouseClick += (_, e) =>
        {
            if (e.Button == Forms.MouseButtons.Left) onClick();
        };
    }

    public void ShowState(IpcTrayState state)
    {
        // NotifyIcon.Text is limited to 63 characters; ours are far shorter.
        _icon.Text = state.MinutesRemaining > 0
            ? Strings.Remaining(state.MinutesRemaining)
            : Strings.TrayActive;
    }

    public void ShowDisconnected() => _icon.Text = Strings.TrayDisconnected;

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
        _image.Dispose();
    }
}
