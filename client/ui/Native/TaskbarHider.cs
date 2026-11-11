using System.Runtime.InteropServices;

namespace Vigil.Ui.Native;

public static class TaskbarHider
{
    private const int SW_HIDE = 0;
    private const int SW_SHOW = 5;

    // SetWindowPos flags
    private const uint SWP_NOMOVE      = 0x0002;
    private const uint SWP_NOSIZE      = 0x0001;
    private const uint SWP_NOZORDER    = 0x0004;
    private const uint SWP_SHOWWINDOW  = 0x0040;
    private const uint SWP_FRAMECHANGED= 0x0020;   // forces WM_NCCALCSIZE → DWM refresh

    // WM_SETTINGCHANGE (26): Explorer rereads the shell state and redraws the taskbar. Needed on Windows 11,
    // where the taskbar is hosted in WinUI XAML Islands.
    private const uint WM_SETTINGCHANGE = 0x001A;

    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern IntPtr FindWindow(string? lpClassName, string? lpWindowName);

    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern IntPtr FindWindowEx(
        IntPtr hwndParent,
        IntPtr hwndChildAfter,
        string? lpszClass,
        string? lpszWindow);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(
        IntPtr hWnd, IntPtr hWndInsertAfter,
        int X, int Y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessage(
        IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

    /// <summary>
    /// Hides every taskbar window that exists right now (primary, Start button, secondary monitors).
    /// Idempotent and cheap: TopmostEnforcer calls it every 500 ms while locked, since Explorer may create or recreate
    /// these windows at any time (boot, Explorer restart). No handles are kept: RestoreAll finds them again.
    /// </summary>
    public static void Hide()
    {
        var taskbar = FindWindow("Shell_TrayWnd", null);
        if (taskbar != IntPtr.Zero) ShowWindow(taskbar, SW_HIDE);

        var startBtn = FindWindow("Start", null);
        if (startBtn != IntPtr.Zero) ShowWindow(startBtn, SW_HIDE);

        var secondary = IntPtr.Zero;
        while ((secondary = FindWindowEx(IntPtr.Zero, secondary, "Shell_SecondaryTrayWnd", null)) != IntPtr.Zero)
            ShowWindow(secondary, SW_HIDE);
    }

    /// <summary>
    /// Restores every taskbar window via a fresh FindWindow. Called on unlock and when vigil_ui
    /// starts (a previous instance killed while locked left the taskbar hidden).
    /// </summary>
    public static void RestoreAll()
    {
        var taskbar = FindWindow("Shell_TrayWnd", null);
        if (taskbar != IntPtr.Zero) RestoreWindow(taskbar);

        var startBtn = FindWindow("Start", null);
        if (startBtn != IntPtr.Zero) RestoreWindow(startBtn);

        var secondary = IntPtr.Zero;
        while ((secondary = FindWindowEx(IntPtr.Zero, secondary, "Shell_SecondaryTrayWnd", null)) != IntPtr.Zero)
            RestoreWindow(secondary);

        try
        {
            var tray = FindWindow("Shell_TrayWnd", null);
            if (tray != IntPtr.Zero)
                PostMessage(tray, WM_SETTINGCHANGE, IntPtr.Zero, IntPtr.Zero);
        }
        catch { /* best effort: Explorer may be restarting */ }
    }

    private static void RestoreWindow(IntPtr h)
    {
        try
        {
            ShowWindow(h, SW_SHOW);
            SetWindowPos(h, IntPtr.Zero, 0, 0, 0, 0,
                SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_SHOWWINDOW | SWP_FRAMECHANGED);
        }
        catch { /* best effort: Explorer may be restarting */ }
    }
}

