using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;

namespace Vigil.Ui.Native;

/// <summary>
/// Co 500ms re-asserts topmost + foreground na liście podanych okien lockscreen.
/// Defense in depth — gdy inny proces ukradł focus albo otworzył okno z WS_EX_TOPMOST
/// nad nami, pół sekundy później jesteśmy z powrotem na wierzchu.
///
/// W przeciwieństwie do zwykłego SetWindowPos+HWND_TOPMOST, używamy AttachThreadInput
/// hack żeby obejść Windows "foreground lock timeout" (ForegroundLockTimeout) — bez tego
/// SetForegroundWindow często cicho fail-uje gdy ostatni input użytkownika był w innej apce.
///
/// Lifecycle: Start() po creation lockscreen, Stop()+Dispose() przy unlock.
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
        // 500ms zamiast 1s — gdy dziecko uruchomi program przez taskbar, mamy ≤500ms
        // visible "obcego" okna zanim wracamy na wierzch. Wystarczająco szybko żeby
        // dziecko nie zdążyło wpisać niczego istotnego.
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _timer.Tick += (_, _) => ReassertAll();
    }

    public void Track(Window w)
    {
        _windows.Add(w);
        // Od razu też wyślij to topmost — initial assert
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
        // Re-hide taskbar — Explorer/system mogą przywrócić visibility (zwłaszcza zaraz po
        // logowaniu lub po wybudzeniu z sleep). Idempotent, bezpieczne wołać co tick.
        TaskbarHider.Hide();
    }

    private static void ReassertSingle(Window w)
    {
        try
        {
            var hwnd = new WindowInteropHelper(w).Handle;
            if (hwnd == IntPtr.Zero) return;

            // Krok 1: re-topmost (Z-order). SWP_NOACTIVATE — nie wymuszaj focus tutaj,
            // robimy to w kroku 2 z AttachThreadInput hack.
            SetWindowPos(hwnd, HWND_TOPMOST, 0, 0, 0, 0,
                SWP_NOSIZE | SWP_NOMOVE | SWP_NOACTIVATE);

            // Krok 2: jeśli inne okno ma fokus, wymuś nasze. Bez tego SetForegroundWindow
            // zwykle silently fail-uje bo Windows "foreground lock" blokuje aplikacje
            // które nie były ostatnio user-activated.
            var fg = GetForegroundWindow();
            if (fg == hwnd) return;  // już mamy fokus, nic do roboty

            var fgThread = GetWindowThreadProcessId(fg, out _);
            var myThread = GetCurrentThreadId();

            if (fgThread != 0 && fgThread != myThread)
            {
                // AttachThreadInput: tymczasowo "łączymy" message queue wątku
                // foreground z naszym → Windows zezwala SetForegroundWindow.
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
                // Już w naszym wątku lub fgThread niedostępny — single attempt
                SetForegroundWindow(hwnd);
                BringWindowToTop(hwnd);
            }
        }
        catch
        {
            // Ignore — okno mogło zostać zamknięte między .Add a tick
        }
    }

    public void Dispose()
    {
        _timer.Stop();
        _windows.Clear();
    }
}
