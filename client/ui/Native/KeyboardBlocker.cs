using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Windows.Input;

namespace Vigil.Ui.Native;

/// <summary>
/// Niskopoziomowy keyboard hook (WH_KEYBOARD_LL) blokujący kombinacje umożliwiające ucieczkę
/// z lockscreen:
/// - Win (LeftWindows / RightWindows) — wszystkie kombinacje Win+X
/// - Alt+Tab, Win+Tab (task switcher)
/// - Alt+F4 (close window)
/// - Alt+Esc (switch window)
/// - Ctrl+Esc (start menu)
/// - Ctrl+Shift+Esc (Task Manager DIRECT — pomija DisableTaskMgr policy w niektórych Windows)
/// - F1 (Help — może otworzyć Edge browser z online help)
/// - F11 (full-screen toggle — w niektórych appkach pomaga uciec)
/// - PrintScreen / Pause (zablokowane defensywnie — niezgrabne ucieczki rzadkie)
///
/// CTRL+ALT+DEL nie da się zablokować (kernel SAS). Pokazuje Windows secure attention sequence
/// z opcjami "Lock", "Switch user", "Sign out", "Task Manager", "Change password".
/// Część z nich blokowana przez registry (DisableTaskMgr, NoLogoff) ustawione przez installer.
///
/// Hook musi być callbackowany w tym samym threadzie co GetMessage loop. WPF Application
/// queue ma własne pump, więc callback działa naturalnie z Application.Current.Dispatcher.
///
/// IMPORTANT: hook jest globalny (system-wide). Trzeba pamiętać żeby Unhook przy zamykaniu,
/// inaczej zostanie zombie.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class KeyboardBlocker : IDisposable
{
    private const int WH_KEYBOARD_LL = 13;
    private const int WM_KEYDOWN = 0x0100;
    private const int WM_SYSKEYDOWN = 0x0104;
    private const int WM_KEYUP = 0x0101;
    private const int WM_SYSKEYUP = 0x0105;

    private const uint LLKHF_ALTDOWN = 0x20;

    private IntPtr _hookId = IntPtr.Zero;
    private readonly LowLevelKeyboardProc _proc;  // GC pin — bez tego callback by zniknął
    private bool _disposed;

    public KeyboardBlocker()
    {
        _proc = HookCallback;
    }

    public void Install()
    {
        if (_hookId != IntPtr.Zero) return;
        // Our own exe module. Not via Process.MainModule: that opens the process by PID, and vigil_ui
        // runs with a restrictive process DACL (UiProcessLauncher).
        _hookId = SetWindowsHookEx(WH_KEYBOARD_LL, _proc, GetModuleHandle(null), 0);
        if (_hookId == IntPtr.Zero)
        {
            throw new System.ComponentModel.Win32Exception(
                Marshal.GetLastWin32Error(),
                "Nie udało się zainstalować low-level keyboard hook.");
        }
    }

    public void Uninstall()
    {
        if (_hookId != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_hookId);
            _hookId = IntPtr.Zero;
        }
    }

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode < 0) return CallNextHookEx(_hookId, nCode, wParam, lParam);

        // Tylko key-down/sys-key-down — key-up nie blokujemy (źle działa z modyfierami)
        var msg = wParam.ToInt32();
        if (msg != WM_KEYDOWN && msg != WM_SYSKEYDOWN)
        {
            return CallNextHookEx(_hookId, nCode, wParam, lParam);
        }

        var info = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
        var key = KeyInterop.KeyFromVirtualKey((int)info.vkCode);

        // Win key (osobny modifier — nie pojawia się w Modifiers properties dla low-level)
        if (key == Key.LWin || key == Key.RWin)
        {
            return (IntPtr)1;  // 1 = swallow
        }

        // Alt down? Sprawdzamy przez flag w hook info (LLKHF_ALTDOWN) bo Keyboard.Modifiers
        // w callbacku z low-level hook nie zawsze zwraca prawdę.
        var altDown = (info.flags & LLKHF_ALTDOWN) != 0;
        var ctrlDown = (GetKeyState(0x11) & 0x8000) != 0;  // VK_CONTROL

        // Alt+Tab, Alt+F4, Alt+Esc (przełącznik aplikacji)
        if (altDown && (key == Key.Tab || key == Key.F4 || key == Key.Escape))
        {
            return (IntPtr)1;
        }

        // Ctrl+Esc (start menu) i Ctrl+Shift+Esc (Task Manager bez SAS)
        if (ctrlDown && key == Key.Escape)
        {
            return (IntPtr)1;
        }

        // F1 (Help — w niektórych kontekstach otwiera Edge browser online), F11 (full-screen
        // toggle), F12 (dev tools w przeglądarkach — irrelevant w lockscreen ale defensywnie).
        if (key == Key.F1 || key == Key.F11 || key == Key.F12)
        {
            return (IntPtr)1;
        }

        // PrintScreen — zostawiamy, bo screenshot nie pomaga w obejściu lockscreen,
        // a dziecko z dostępem do tego wciąż nie ma jak zrobić nic z obrazem.

        return CallNextHookEx(_hookId, nCode, wParam, lParam);
    }

    public void Dispose()
    {
        if (_disposed) return;
        Uninstall();
        _disposed = true;
        GC.SuppressFinalize(this);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KBDLLHOOKSTRUCT
    {
        public uint vkCode;
        public uint scanCode;
        public uint flags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn,
        IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);

    [DllImport("user32.dll")]
    private static extern short GetKeyState(int nVirtKey);
}
