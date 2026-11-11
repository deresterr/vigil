using System.Runtime.InteropServices;

namespace Vigil.Ui.Native;

/// <summary>
/// The lock windows' frame, in the screen's own pixels (#120):
/// - placement: <c>Screen.Bounds</c> is in pixels while WPF's Left/Width are device-independent units, so at a
///   display scale above 100 % a window sized from Bounds came out larger than the screen and off-center (the
///   clock and "Shut down" fell off the edge). SetWindowPos takes the same pixels Bounds gives.
/// - no system menu: a WindowStyle=None window keeps WS_SYSMENU, and Alt+Space opened Move and Close on the
///   overlay. The style bit goes, and <see cref="IsBlockedSysCommand"/> refuses what is left of it.
/// Another process of the same user can move, hide or minimize any window of the desktop; TopmostEnforcer puts
/// the overlay back with <see cref="Restore"/> every 500 ms. Friction, like the rest of the overlay.
/// </summary>
public static class WindowFrame
{
    public const int WM_SYSCOMMAND = 0x0112;

    private const int GWL_STYLE = -16;
    private const long WS_SYSMENU = 0x00080000;
    private const uint SWP_NOACTIVATE = 0x0010;
    private const uint SWP_SHOWWINDOW = 0x0040;
    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_NOSIZE = 0x0001;
    private const int SW_RESTORE = 9;
    private static readonly IntPtr HWND_TOPMOST = new(-1);

    // SC_SIZE, SC_MOVE, SC_MINIMIZE, SC_MAXIMIZE, SC_CLOSE, SC_RESTORE, SC_KEYMENU, SC_MOUSEMENU (WM_SYSCOMMAND
    // wParam & 0xFFF0): every way the system menu and its shortcuts change a window.
    private static readonly int[] BlockedCommands = { 0xF000, 0xF010, 0xF020, 0xF030, 0xF060, 0xF120, 0xF100, 0xF090 };

    public static bool IsBlockedSysCommand(IntPtr wParam) => Array.IndexOf(BlockedCommands, (int)wParam & 0xFFF0) >= 0;

    public static void RemoveSystemMenu(IntPtr hwnd)
    {
        var style = (long)GetWindowLongPtr(hwnd, GWL_STYLE);
        if ((style & WS_SYSMENU) != 0) SetWindowLongPtr(hwnd, GWL_STYLE, new IntPtr(style & ~WS_SYSMENU));
    }

    /// <summary>Exactly over <paramref name="bounds"/> (pixels), on top; before the window is first shown (WPF
    /// shows it itself).</summary>
    public static void Place(IntPtr hwnd, System.Drawing.Rectangle bounds) =>
        SetWindowPos(hwnd, HWND_TOPMOST, bounds.Left, bounds.Top, bounds.Width, bounds.Height, SWP_NOACTIVATE);

    /// <summary>On top, shown, restored and exactly over <paramref name="bounds"/> (pixels).</summary>
    public static void Restore(IntPtr hwnd, System.Drawing.Rectangle bounds)
    {
        if (IsIconic(hwnd)) ShowWindow(hwnd, SW_RESTORE);
        var flags = SWP_NOACTIVATE | SWP_SHOWWINDOW;
        if (GetWindowRect(hwnd, out var r) && r.Left == bounds.Left && r.Top == bounds.Top
            && r.Right == bounds.Right && r.Bottom == bounds.Bottom)
            flags |= SWP_NOMOVE | SWP_NOSIZE;  // in place: only the z-order
        SetWindowPos(hwnd, HWND_TOPMOST, bounds.Left, bounds.Top, bounds.Width, bounds.Height, flags);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left, Top, Right, Bottom;
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
}
