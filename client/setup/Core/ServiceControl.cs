using System.Diagnostics;
using System.ServiceProcess;
using Vigil.Shared;

namespace Vigil.Setup.Core;

/// <summary>
/// Registering, starting, stopping and deleting vigil_watchdog. The service DACL gives administrators
/// query/start only, so stop and delete go through a one-off SYSTEM task (SY: GenericAll): a normal SCM
/// stop, the watchdog says goodbye and the server logs tamper_service_stopped instead of a restart. Killing
/// the process is only the fallback. Port of Invoke-ScAsSystem / Stop-VigilService (#45).
/// </summary>
internal static class ServiceControl
{
    private static string Name => VigilLayout.ServiceName;

    public static ServiceControllerStatus? Status()
    {
        using var sc = new ServiceController(Name);
        try { return sc.Status; }
        catch (InvalidOperationException) { return null; }
    }

    private static bool WaitFor(Func<ServiceControllerStatus?, bool> condition, int seconds)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        do
        {
            if (condition(Status())) return true;
            Thread.Sleep(500);
        } while (DateTime.UtcNow < deadline);
        return false;
    }

    /// <summary>"sc.exe &lt;args&gt;" as SYSTEM through a one-off task; returns once the task is started,
    /// the caller waits for the service state. The task is removed when <paramref name="waitUntil"/> holds
    /// or the time is up.</summary>
    private static bool ScAsSystem(string args, Func<ServiceControllerStatus?, bool> waitUntil, int seconds)
    {
        var task = $"_vigil_sc_{Random.Shared.Next(100_000, 999_999)}";
        ScheduledTasks.Create(task, ScheduledTasks.OneOffSystemTaskXml("sc.exe", args));
        try
        {
            ScheduledTasks.Run(task);
            return WaitFor(waitUntil, seconds);
        }
        finally { ScheduledTasks.Delete(task); }
    }

    /// <summary>Stop as SYSTEM (15 s: the goodbye waits up to 3 s for the server), else kill.</summary>
    public static void Stop(IReport r)
    {
        var status = Status();
        if (status is null) { r.Info(Strings.ServiceAbsent); return; }
        if (status == ServiceControllerStatus.Stopped) { r.Info(Strings.ServiceAlreadyStopped); return; }

        try
        {
            if (ScAsSystem($"stop {Name}", s => s is ServiceControllerStatus.Stopped or null, 15))
            {
                r.Check(CheckLevel.Pass, Strings.ServiceStoppedAsSystem);
                return;
            }
        }
        catch (SetupException ex) { r.Info(ex.Message); }

        // No goodbye: the server sees a restart without one. Admins may kill a SYSTEM process.
        foreach (var p in Process.GetProcessesByName("vigil_watchdog"))
        {
            try { p.Kill(); } catch (Exception) { }
            p.Dispose();
        }
        if (WaitFor(s => s is ServiceControllerStatus.Stopped or null, 8))
        {
            r.Check(CheckLevel.Warn, Strings.ServiceKilled);
            return;
        }
        throw new SetupException(Strings.ServiceStopFailed);
    }

    /// <summary>sc.exe delete as SYSTEM (admins lack SD). True when the service is gone.</summary>
    public static bool Delete(IReport r)
    {
        if (Status() is null) return true;
        try { return ScAsSystem($"delete {Name}", s => s is null, 10); }
        catch (SetupException ex) { r.Info(ex.Message); return Status() is null; }
    }

    /// <summary>
    /// CreateService through sc.exe: the path quoted (the quotes reach sc.exe intact from .NET, unlike
    /// PowerShell 5.1), automatic start, recovery restart ×3 after 60 s without reboot (a crash at start
    /// must not become a reboot loop), recovery on a non-zero exit too.
    /// </summary>
    public static void Create(IReport r)
    {
        var exe = VigilLayout.PathOf(VigilLayout.WatchdogExe);
        Sc("create", Name, "binPath=", $"\"{exe}\"", "start=", "auto", "DisplayName=", VigilLayout.ServiceDisplayName);
        Sc("description", Name, "Service for parental time control");
        SetRecovery();
        r.Check(CheckLevel.Pass, Strings.ServiceCreated);
    }

    /// <summary>Restart ×3 after 60 s, reset after a day, also on a non-zero exit (installer and guardian).
    /// sc.exe changes the config: SYSTEM only once the DACL is set.</summary>
    public static void SetRecovery()
    {
        var delay = VigilLayout.RecoveryDelayMs;
        Sc("failure", Name, "reset=", VigilLayout.RecoveryResetSeconds.ToString(),
            "actions=", string.Join('/', Enumerable.Repeat($"restart/{delay}", VigilLayout.RecoveryRestarts)));
        Sc("failureflag", Name, "1");
    }

    /// <summary>Automatic start (the guardian, as SYSTEM: the DACL denies admins change-config).</summary>
    public static void SetAutoStart() => Sc("config", Name, "start=", "auto");

    /// <summary>Admins: query and start only (VigilLayout.ServiceSddl). Friction: an admin can still kill the
    /// process or act as SYSTEM, which is what this installer does. Detection is the server's job.</summary>
    public static void RestrictDacl(IReport r)
    {
        var (rc, output) = NativeTool.Run("sc.exe", "sdset", Name, VigilLayout.ServiceSddl);
        if (rc == 0) r.Check(CheckLevel.Pass, Strings.ServiceDaclSet);
        else r.Check(CheckLevel.Fail, Strings.ServiceDaclFailed(rc, output.Trim()));
    }

    /// <summary>Admins may start it; SYSTEM task as the fallback.</summary>
    public static bool Start(IReport r)
    {
        try
        {
            using var sc = new ServiceController(Name);
            if (sc.Status != ServiceControllerStatus.Running) sc.Start();
            sc.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(15));
            r.Check(CheckLevel.Pass, Strings.ServiceStarted);
            return true;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ServiceProcess.TimeoutException)
        {
            r.Info(ex.Message);
        }
        try
        {
            if (ScAsSystem($"start {Name}", s => s == ServiceControllerStatus.Running, 15))
            {
                r.Check(CheckLevel.Pass, Strings.ServiceStartedAsSystem);
                return true;
            }
        }
        catch (SetupException ex) { r.Info(ex.Message); }
        return false;
    }

    private static void Sc(params string[] args)
    {
        var (rc, output) = NativeTool.Run("sc.exe", args);
        if (rc != 0) throw new SetupException(Strings.ScFailed(args[0], rc, output.Trim()));
    }
}
