using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;

namespace Vigil.Ui.Native;

/// <summary>
/// Every 500 ms puts the lock windows back on top and in the foreground: when another process took the
/// focus or opened a topmost window over them, half a second later they are on top again.
///
/// SetForegroundWindow alone often fails silently (the foreground lock timeout, when the user's last input
/// went to another app), so it runs with the foreground thread's input attached (AttachThreadInput).
///
/// Start() once the windows exist, Stop() + Dispose() on unlock.
/// </summary>
public sealed class TopmostEnforcer : IDisposable
{
    private const uint SWP_NOSIZE       = 0x0001;
    private const uint SWP_NOMOVE       = 0x0002;
    private const uint SWP_NOACTIVATE   = 0x0010;
    private const uint SWP_SHOWWINDOW   = 0x0040;
    private static readonly IntPtr HWND_TOPMOST = new(-1);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter,
        int X, int Y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo,
        [MarshalAs(UnmanagedType.Bool)] bool fAttach);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool BringWindowToTop(IntPtr hWnd);

    private readonly DispatcherTimer _timer;
    private readonly List<Window> _windows = new();

    public TopmostEnforcer()
    {
        // 500 ms: a window that got over the lock is visible for at most half a second.
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _timer.Tick += (_, _) => ReassertAll();
    }

    public void Track(Window w)
    {
        _windows.Add(w);
        // On top at once, not only at the next tick
        ReassertSingle(w);
    }

    public void Start() => _timer.Start();
    public void Stop()  => _timer.Stop();

    /// <summary>Stop and forget all windows (overlay hidden; the process keeps running).</summary>
    public void Clear()
    {
        _timer.Stop();
        _windows.Clear();
    }

    private void ReassertAll()
    {
        foreach (var w in _windows)
        {
            ReassertSingle(w);
        }
        // Explorer may show the taskbar again (right after logon, after sleep); hiding is idempotent.
        TaskbarHider.Hide();
    }

    private static void ReassertSingle(Window w)
    {
        try
        {
            var hwnd = new WindowInteropHelper(w).Handle;
            if (hwnd == IntPtr.Zero) return;

            // 1. Z-order only (SWP_NOACTIVATE); the focus is step 2.
            SetWindowPos(hwnd, HWND_TOPMOST, 0, 0, 0, 0,
                SWP_NOSIZE | SWP_NOMOVE | SWP_NOACTIVATE);

            // 2. Take the focus back from another window.
            var fg = GetForegroundWindow();
            if (fg == hwnd) return;

            var fgThread = GetWindowThreadProcessId(fg, out _);
            var myThread = GetCurrentThreadId();

            if (fgThread != 0 && fgThread != myThread)
            {
                // With the foreground thread's input attached Windows allows SetForegroundWindow.
                if (AttachThreadInput(fgThread, myThread, true))
                {
                    try
                    {
                        SetForegroundWindow(hwnd);
                        BringWindowToTop(hwnd);
                    }
                    finally
                    {
                        AttachThreadInput(fgThread, myThread, false);
                    }
                }
            }
            else
            {
                // Our own thread, or no foreground thread: a plain attempt
                SetForegroundWindow(hwnd);
                BringWindowToTop(hwnd);
            }
        }
        catch
        {
            // The window may have closed since it was tracked.
        }
    }

    public void Dispose()
    {
        _timer.Stop();
        _windows.Clear();
    }
}
