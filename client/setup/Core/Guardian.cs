using System.Diagnostics;
using System.Security.Cryptography;
using System.ServiceProcess;
using System.Text;
using Microsoft.Win32;
using Vigil.Shared;

namespace Vigil.Setup.Core;

/// <summary>
/// `vigil_setup.exe guardian`, run by the task "Vigil Guardian" (SYSTEM, at startup + every 5 min): puts back
/// what keeps vigil_watchdog running and reports every repair. Port of vigil_guardian.ps1 (#65).
///
/// Threat model: friction + detection. An elevated child can disable the task too; until then every change
/// below is undone and the watchdog reports it to the parent's journal as tamper_guardian_repaired (#36).
///
/// Checks, in this order (a replaced binary is restored with the service stopped and started only after the
/// other repairs: an IFEO debugger left in place would hijack that start):
///   7003 binary_replaced      vigil_watchdog.exe hash != guardian.dat → restore from backup\, start last
///   7004 ifeo_debugger        IFEO Debugger on vigil_watchdog.exe → removed
///   7002 service_disabled     start type not automatic → automatic
///   7006 recovery_missing     restart actions not as installed → the installer's ones
///   7005 safeboot_missing     SafeBoot Minimal/Network entries → recreated
///   7001 service_not_running  still stopped after the recovery delay → started
///
/// Event Log (Application, source "Vigil Guardian"): those IDs and the level are the contract with the
/// watchdog (GuardianReporter, VigilLayout.GuardianChecks): Warning = repaired, Error = repair failed. Other
/// IDs are diagnostics only. Messages are technical English, like the watchdog's own log: nobody reads them
/// but support. Everything is read from the registry and APIs, nothing from localized tool output.
/// </summary>
public static class Guardian
{
    private const int EventCrash = 9999;
    private const int EventNoService = 1099;
    private const int EventReferenceUnreadable = 1003;

    /// <summary>Service recovery restarts a crashed or killed watchdog after 60 s; wait that out before calling
    /// a stopped service tampering (update-config also stops it for a moment).</summary>
    private static readonly TimeSpan RecoveryWait = TimeSpan.FromSeconds(75);

    public static int Run()
    {
        try
        {
            EnsureSource();
            Check();
            return 0;
        }
        catch (Exception ex)
        {
            Write(EventLogEntryType.Error, EventCrash, $"Guardian crash: {ex}");
            return 1;
        }
    }

    private static void Check()
    {
        if (WatchdogService.Read() is not { } svc)
        {
            // Nothing to repair without the service; reinstalling is the installer's job.
            Write(EventLogEntryType.Error, EventNoService, $"Service {VigilLayout.ServiceName} does not exist");
            return;
        }

        // Restored now, started last (reported then: the repair is only done once it runs again).
        var restored = false;
        if (!BinaryIntact())
        {
            try
            {
                RestoreBinary();
                restored = true;
            }
            catch (Exception ex)
            {
                Write(EventLogEntryType.Error, 7003, $"{BinaryReplaced} - repair failed: {ex.Message}");
            }
        }
        if (IfeoDebugger() is { } debugger)
            Repair(7004, $"IFEO Debugger on vigil_watchdog.exe ({debugger})", RemoveIfeoDebugger);
        if (svc.Start != 2)
            Repair(7002, $"service start type {svc.Start?.ToString() ?? "missing"}, not automatic", ServiceControl.SetAutoStart);
        if (!svc.RecoveryAsInstalled)
            Repair(7006, "service recovery is not restart x3", ServiceControl.SetRecovery);
        if (!SafeBoot.Present())
            Repair(7005, "SafeBoot entries missing", SafeBoot.Write);
        if (restored)
            Repair(7003, BinaryReplaced, StartService);
        else if (!IsUp())
        {
            Thread.Sleep(RecoveryWait);
            if (!IsUp()) Repair(7001, "service was stopped", StartService);
        }
    }

    private const string BinaryReplaced = "vigil_watchdog.exe was replaced";

    /// <summary>Runs <paramref name="repair"/> and logs the finding for the watchdog: Warning if it worked,
    /// Error if it threw.</summary>
    private static void Repair(int eventId, string what, Action repair)
    {
        try
        {
            repair();
            Write(EventLogEntryType.Warning, eventId, $"{what} - repaired");
        }
        catch (Exception ex)
        {
            Write(EventLogEntryType.Error, eventId, $"{what} - repair failed: {ex.Message}");
        }
    }

    // === Checks and repairs ===

    /// <summary>guardian.dat: SHA-256 hex of vigil_watchdog.exe, DPAPI LocalMachine, written by the installer.
    /// No readable reference: no check (better than a false alert).</summary>
    private static bool BinaryIntact()
    {
        var reference = ReferenceHash();
        if (reference is null) return true;
        var exe = VigilLayout.PathOf(VigilLayout.WatchdogExe);
        if (!File.Exists(exe)) return false;
        return Sha256(exe).Equals(reference, StringComparison.OrdinalIgnoreCase);
    }

    private static string? ReferenceHash()
    {
        var file = VigilLayout.PathOf(VigilLayout.GuardianFile);
        if (!File.Exists(file)) return null;
        try
        {
            return Encoding.UTF8.GetString(
                ProtectedData.Unprotect(File.ReadAllBytes(file), null, DataProtectionScope.LocalMachine)).Trim();
        }
        catch (Exception ex) when (ex is CryptographicException or IOException)
        {
            Write(EventLogEntryType.Warning, EventReferenceUnreadable, $"guardian.dat unreadable: {ex.Message}");
            return null;
        }
    }

    private static void RestoreBinary()
    {
        var backup = VigilLayout.BackupWatchdogPath;
        if (!File.Exists(backup)) throw new IOException($"no backup {backup}");
        // SYSTEM may stop it (service DACL: SY GenericAll): a normal stop, the watchdog says goodbye.
        using (var sc = new ServiceController(VigilLayout.ServiceName))
        {
            if (sc.Status != ServiceControllerStatus.Stopped)
            {
                sc.Stop();
                sc.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(20));
            }
        }
        File.Copy(backup, VigilLayout.PathOf(VigilLayout.WatchdogExe), overwrite: true);
    }

    private static string? IfeoDebugger()
    {
        using var key = Registry.LocalMachine.OpenSubKey(VigilLayout.IfeoKey);
        return key?.GetValue("Debugger") is string { Length: > 0 } d ? d : null;
    }

    private static void RemoveIfeoDebugger()
    {
        using var key = Registry.LocalMachine.OpenSubKey(VigilLayout.IfeoKey, writable: true);
        key?.DeleteValue("Debugger", throwOnMissingValue: false);
    }

    private static bool IsUp() =>
        ServiceControl.Status() is ServiceControllerStatus.Running or ServiceControllerStatus.StartPending;

    private static void StartService()
    {
        using var sc = new ServiceController(VigilLayout.ServiceName);
        if (sc.Status != ServiceControllerStatus.Running) sc.Start();
        sc.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(20));
    }

    private static string Sha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    // === Event Log ===

    private static void EnsureSource()
    {
        try
        {
            if (!EventLog.SourceExists(VigilLayout.GuardianEventSource))
                EventLog.CreateEventSource(VigilLayout.GuardianEventSource, "Application");
        }
        catch (Exception) { /* written below anyway; Write swallows a failure */ }
    }

    private static void Write(EventLogEntryType type, int eventId, string message)
    {
        try { EventLog.WriteEntry(VigilLayout.GuardianEventSource, message, type, eventId); }
        catch (Exception) { /* nowhere else to report it */ }
    }
}

/// <summary>The service's SafeBoot Minimal/Network entries: without them a reboot into safe mode starts no
/// watchdog. Shared by the installer, the guardian and verify.</summary>
public static class SafeBoot
{
    public static bool Present(string key)
    {
        using var k = Registry.LocalMachine.OpenSubKey(key);
        return k?.GetValue("") as string == "Service";
    }

    public static bool Present() => VigilLayout.SafeBootKeys.All(Present);

    public static void Write()
    {
        foreach (var key in VigilLayout.SafeBootKeys)
        {
            using var k = Registry.LocalMachine.CreateSubKey(key, writable: true);
            k.SetValue("", "Service");
        }
    }
}
