using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Win32;

namespace Vigil.Shared;

/// <summary>
/// Everything the installer (vigil_setup) and the watchdog must agree on about an installation: paths,
/// file names, the service, the guardian task and its event IDs, DPAPI entropy, the per-user policies.
/// Linked into both projects (<Compile Include Link>) like IpcMessages.cs.
///
/// File names and entropy strings are a compatibility contract with existing installs (CLAUDE.md):
/// changing one breaks every PC installed before the change.
/// </summary>
public static class VigilLayout
{
    // === Files under C:\ProgramData\Vigil ===

    public const string InstallDir = @"C:\ProgramData\Vigil";

    public const string WatchdogExe = "vigil_watchdog.exe";
    public const string UiExe = "vigil_ui.exe";
    public const string SetupExe = "vigil_setup.exe";

    /// <summary>
    /// WPF's native libraries, next to vigil_ui.exe instead of extracted to the child's %TEMP% at run time
    /// (D24: a DLL planted there would load into the lock screen). The package carries them;
    /// build-package.ps1 fails when the publish output differs from this list.
    /// </summary>
    public static readonly string[] UiNativeLibraries =
    {
        "D3DCompiler_47_cor3.dll", "PenImc_cor3.dll", "PresentationNative_cor3.dll", "vcruntime140_cor3.dll",
        "wpfgfx_cor3.dll",
    };
    public const string ConfigFile = "vigil.toml";
    /// <summary>The device's signing key, DPAPI LocalMachine with <see cref="SigningKeyEntropy"/>.</summary>
    public const string SigningFile = "signing.dat";
    /// <summary>The parent's TOTP secret for offline unlock, DPAPI LocalMachine with <see cref="TotpEntropy"/>.</summary>
    public const string TotpFile = "totp.dat";
    /// <summary>SHA-256 hex of vigil_watchdog.exe, DPAPI LocalMachine without entropy (the guardian's reference).</summary>
    public const string GuardianFile = "guardian.dat";
    public const string BackupDir = "backup";

    public const string SigningKeyEntropy = "Vigil-SigningKey-v1";
    public const string TotpEntropy = "Vigil-Totp-v1";

    /// <summary>DPAPI secrets: SYSTEM full, Administrators read (<see cref="FileAcls"/>).</summary>
    public static readonly string[] SecretFiles = { SigningFile, TotpFile };

    public static string PathOf(string name) => Path.Combine(InstallDir, name);
    public static string BackupWatchdogPath => Path.Combine(InstallDir, BackupDir, WatchdogExe);

    // === Service ===

    public const string ServiceName = "vigil_watchdog";
    public const string ServiceDisplayName = "Vigil Watchdog";

    /// <summary>
    /// Administrators, interactive and service users: query config/status, enumerate dependents, start,
    /// interrogate, user control, read. No stop (WP), pause (DT), change config (DC), delete (SD), WD/WO.
    /// SYSTEM: everything. Stop and delete go through a one-off SYSTEM task. Friction, not protection.
    /// </summary>
    public const string ServiceSddl =
        "D:(A;;CCLCSWRPLOCRRC;;;BA)(A;;CCLCSWRPLOCRRC;;;IU)(A;;CCLCSWRPLOCRRC;;;SU)(A;;GA;;;SY)";

    /// <summary>Service recovery: restart ×3 after 60 s, failure count reset after a day, no reboot.</summary>
    public const int RecoveryRestarts = 3;
    public const int RecoveryDelayMs = 60_000;
    public const int RecoveryResetSeconds = 86_400;

    public const string ServiceKey = @"SYSTEM\CurrentControlSet\Services\" + ServiceName;
    public static readonly string[] SafeBootKeys =
    {
        @"SYSTEM\CurrentControlSet\Control\SafeBoot\Minimal\" + ServiceName,
        @"SYSTEM\CurrentControlSet\Control\SafeBoot\Network\" + ServiceName,
    };
    public const string IfeoKey =
        @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Image File Execution Options\" + WatchdogExe;

    /// <summary>Every process name of ours (verify: none left after uninstall).</summary>
    public static readonly string[] ProcessNames = { "vigil_watchdog", "vigil_ui" };

    // === Event Log (Application) ===

    public const string WatchdogEventSource = "Vigil Watchdog";
    public const string GuardianEventSource = "Vigil Guardian";
    /// <summary>Both sources, removed on uninstall (the writers create them again on their first entry).</summary>
    public static readonly string[] EventSources = { WatchdogEventSource, GuardianEventSource };

    /// <summary>The guardian's findings: event ID → check code sent to the server (GUARDIAN_CHECKS).
    /// Warning = repaired, Error = repair failed.</summary>
    public static readonly IReadOnlyDictionary<int, string> GuardianChecks = new Dictionary<int, string>
    {
        [7001] = "service_not_running",
        [7002] = "service_disabled",
        [7003] = "binary_replaced",
        [7004] = "ifeo_debugger",
        [7005] = "safeboot_missing",
        [7006] = "recovery_missing",
    };

    // === Scheduled tasks ===

    public const string GuardianTaskName = "Vigil Guardian";
    /// <summary>One-off SYSTEM tasks (stop/delete of the service, ServiceControl.ScAsSystem); none may stay.</summary>
    public const string TempTaskPrefix = "_vigil_sc_";

    // === Per-user policies (HKCU of the child's account and of the Default profile) ===

    /// <summary>
    /// Set on install, checked by verify, reset on uninstall. Only what closes a way out of the lock screen
    /// (D17). The parent maintains the PC through the same account (D12), so each of these hits the parent
    /// too; against an elevated child they are friction only. Key is relative to the hive root.
    /// </summary>
    public static readonly UserPolicy[] UserPolicies =
    {
        // Ctrl+Alt+Del -> Task Manager would run programs over the overlay
        new(PoliciesSystem, "DisableTaskMgr", RegistryValueKind.DWord, 1, null),
        // the child must not change the password of the shared account
        new(PoliciesSystem, "DisableChangePassword", RegistryValueKind.DWord, 1, null),
        // Activation shortcuts of Sticky/Filter/Toggle/Mouse Keys open dialogs that can cover the lock
        // screen or take focus. Only the shortcuts; the features stay in Control Panel.
        new(@"Control Panel\Accessibility\StickyKeys", "Flags", RegistryValueKind.String, "506", "510"),
        new(@"Control Panel\Accessibility\Keyboard Response", "Flags", RegistryValueKind.String, "122", "126"),
        new(@"Control Panel\Accessibility\ToggleKeys", "Flags", RegistryValueKind.String, "58", "62"),
        new(@"Control Panel\Accessibility\MouseKeys", "Flags", RegistryValueKind.String, "58", "62"),
        // Precision touchpad gestures (Task View, Action Center) bypass the keyboard hook
        new(PrecisionTouchPad, "ThreeFingerSlideEnabled", RegistryValueKind.DWord, 0, 1),
        new(PrecisionTouchPad, "FourFingerSlideEnabled", RegistryValueKind.DWord, 0, 1),
        new(PrecisionTouchPad, "ThreeFingerTapEnabled", RegistryValueKind.DWord, 0, 1),
        new(PrecisionTouchPad, "FourFingerTapEnabled", RegistryValueKind.DWord, 0, 1),
    };

    private const string PoliciesSystem = @"Software\Microsoft\Windows\CurrentVersion\Policies\System";
    private const string PrecisionTouchPad = @"Software\Microsoft\Windows\CurrentVersion\PrecisionTouchPad";

    /// <summary>The Default profile's hive: policies for accounts created later.</summary>
    public static string DefaultProfileHive =>
        (Environment.GetEnvironmentVariable("SystemDrive") ?? "C:") + @"\Users\Default\NTUSER.DAT";
}

/// <summary>One per-user registry value. <paramref name="Reset"/>: the uninstall value, null = remove it.</summary>
public sealed record UserPolicy(string Key, string Name, RegistryValueKind Kind, object Value, object? Reset)
{
    /// <summary>"System\DisableTaskMgr": the last key segment and the value name.</summary>
    public string Label => $"{Key[(Key.LastIndexOf('\\') + 1)..]}\\{Name}";
}
