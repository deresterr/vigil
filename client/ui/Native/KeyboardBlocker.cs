using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Windows.Input;

namespace Vigil.Ui.Native;

/// <summary>
/// Low-level keyboard hook (WH_KEYBOARD_LL) swallowing the keys that lead away from the lock screen:
/// - Win (both keys), so every Win+… combination
/// - Alt+Tab, Alt+Esc (switch window), Alt+F4 (close window)
/// - Ctrl+Esc (Start) and Ctrl+Shift+Esc (Task Manager without the SAS screen)
/// - F1 (Help can open a browser), F11 (full screen toggle), F12 (developer tools)
///
/// Ctrl+Alt+Del cannot be blocked (the secure attention sequence); of what its screen offers, the installer's
/// per-user policies close Task Manager and Change password (DisableTaskMgr, DisableChangePassword).
///
/// The hook runs on the thread with the message loop (WPF's dispatcher). It is system-wide: Dispose
/// unhooks it.
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
    private readonly LowLevelKeyboardProc _proc;  // keeps the delegate alive for the native callback
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
                "Could not install the low-level keyboard hook");
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

        // Key-down only: swallowing key-up confuses the modifier state
        var msg = wParam.ToInt32();
        if (msg != WM_KEYDOWN && msg != WM_SYSKEYDOWN)
        {
            return CallNextHookEx(_hookId, nCode, wParam, lParam);
        }

        var info = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
        var key = KeyInterop.KeyFromVirtualKey((int)info.vkCode);

        // Win key (not among the low-level hook's modifiers)
        if (key == Key.LWin || key == Key.RWin)
        {
            return (IntPtr)1;  // 1 = swallow
        }

        // Alt from the hook's own flag (LLKHF_ALTDOWN): Keyboard.Modifiers is not reliable in this callback
        var altDown = (info.flags & LLKHF_ALTDOWN) != 0;
        var ctrlDown = (GetKeyState(0x11) & 0x8000) != 0;  // VK_CONTROL

        // Alt+Tab, Alt+F4, Alt+Esc
        if (altDown && (key == Key.Tab || key == Key.F4 || key == Key.Escape))
        {
            return (IntPtr)1;
        }

        // Ctrl+Esc (Start) and Ctrl+Shift+Esc (Task Manager)
        if (ctrlDown && key == Key.Escape)
        {
            return (IntPtr)1;
        }

        // F1 (Help can open a browser), F11 (full screen toggle), F12 (developer tools)
        if (key == Key.F1 || key == Key.F11 || key == Key.F12)
        {
            return (IntPtr)1;
        }

        // PrintScreen stays: a screenshot is no way out.

        return CallNextHookEx(_hookId, nCode, wParam, lParam);
    }

    public void Dispose()
    {
        if (_disposed) return;
        Uninstall();
        _disposed = true;
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
