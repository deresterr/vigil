using System.Runtime.Versioning;
using Forms = System.Windows.Forms;

namespace Vigil.Ui;

/// <summary>
/// Pause icon in the notification area.
///
///   Left click  — start a pause (the watchdog asks the server); the tooltip says so
///   Hover       — time remaining, or "no limit", refreshed after every heartbeat
/// It is reachable only while unlocked: the lock overlay hides the taskbar.
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
        // NotifyIcon.Text is limited to 63 characters; ours are far shorter. Unlocked with 0 minutes left is a
        // day without a limit (minutes_remaining is 0 then, #106); a used-up limit is a lock.
        _icon.Text = state.MinutesRemaining > 0
            ? Strings.TrayRemaining(state.MinutesRemaining)
            : Strings.TrayNoLimit;
    }

    public void ShowDisconnected() => _icon.Text = Strings.TrayDisconnected;

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
        _image.Dispose();
    }
}
