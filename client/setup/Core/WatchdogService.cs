using System.ComponentModel;
using System.Security.AccessControl;
using System.ServiceProcess;
using Microsoft.Win32;
using Vigil.Setup.Native;
using Vigil.Shared;

namespace Vigil.Setup.Core;

/// <summary>The vigil_watchdog service as Windows has it: SCM state, its registry key, its DACL.</summary>
public sealed class WatchdogService
{
    public ServiceControllerStatus Status { get; private init; }
    /// <summary>The service key's Start value: 2 = automatic.</summary>
    public int? Start { get; private init; }
    public bool DelayedAutoStart { get; private init; }
    public string ImagePath { get; private init; } = "";
    /// <summary>Recovery actions: (type, delay ms); type 1 = restart.</summary>
    public List<(int Type, int DelayMs)> FailureActions { get; private init; } = new();
    public bool FailureActionsOnNonCrash { get; private init; }

    /// <summary>Recovery as the installer sets it: restart ×3, also on a non-zero exit (verify and guardian).</summary>
    public bool RecoveryAsInstalled =>
        FailureActions.Count == VigilLayout.RecoveryRestarts && FailureActions.All(a => a.Type == 1)
        && FailureActionsOnNonCrash;

    /// <returns>null when the service is not registered.</returns>
    public static WatchdogService? Read()
    {
        ServiceControllerStatus status;
        using (var sc = new ServiceController(VigilLayout.ServiceName))
        {
            try { status = sc.Status; }
            catch (InvalidOperationException) { return null; }
        }
        using var key = Registry.LocalMachine.OpenSubKey(VigilLayout.ServiceKey);
        return new WatchdogService
        {
            Status = status,
            Start = key?.GetValue("Start") as int?,
            DelayedAutoStart = key?.GetValue("DelayedAutostart") is 1,
            ImagePath = key?.GetValue("ImagePath", null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string ?? "",
            FailureActions = ParseFailureActions(key?.GetValue("FailureActions") as byte[]),
            FailureActionsOnNonCrash = key?.GetValue("FailureActionsOnNonCrashFailures") is 1,
        };
    }

    public static bool Exists() => Read() is not null;

    /// <summary>
    /// SERVICE_FAILURE_ACTIONS as stored in the service key (language-independent, unlike sc.exe qfailure):
    /// reset period, reboot message, command, action count, actions pointer, then {type, delay} pairs.
    /// </summary>
    public static List<(int Type, int DelayMs)> ParseFailureActions(byte[]? raw)
    {
        var actions = new List<(int Type, int DelayMs)>();
        if (raw is null || raw.Length < 20) return actions;
        var count = BitConverter.ToInt32(raw, 12);
        for (var i = 0; i < count && 20 + 8 * i + 8 <= raw.Length; i++)
            actions.Add((BitConverter.ToInt32(raw, 20 + 8 * i), BitConverter.ToInt32(raw, 24 + 8 * i)));
        return actions;
    }

    /// <summary>The service's DACL in SDDL (D:...), read through the SCM: admins have READ_CONTROL.</summary>
    public static string ReadSddl()
    {
        var scm = Win32.OpenSCManagerW(null, null, Win32.SC_MANAGER_CONNECT);
        if (scm == IntPtr.Zero) throw new Win32Exception();
        try
        {
            var svc = Win32.OpenServiceW(scm, VigilLayout.ServiceName, Win32.READ_CONTROL);
            if (svc == IntPtr.Zero) throw new Win32Exception();
            try
            {
                Win32.QueryServiceObjectSecurity(svc, Win32.DACL_SECURITY_INFORMATION, null, 0, out var needed);
                var buffer = new byte[needed];
                if (!Win32.QueryServiceObjectSecurity(svc, Win32.DACL_SECURITY_INFORMATION, buffer, needed, out _))
                    throw new Win32Exception();
                return new RawSecurityDescriptor(buffer, 0).GetSddlForm(AccessControlSections.Access);
            }
            finally { Win32.CloseServiceHandle(svc); }
        }
        finally { Win32.CloseServiceHandle(scm); }
    }
}
