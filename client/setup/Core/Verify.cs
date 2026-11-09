using System.Diagnostics;
using System.Diagnostics.Eventing.Reader;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.ServiceProcess;
using System.Text;
using System.Text.RegularExpressions;
using Vigil.Shared;

namespace Vigil.Setup.Core;

/// <summary>
/// -Mode verify: reads the installed state and reports one check per line; changes nothing (a user hive
/// that is not loaded is opened as a private app hive and closed). Without an installation it checks that
/// the uninstall left nothing behind. What it cannot see (the lock overlay, the tray, keyboard blocking)
/// stays a manual test. A diagnostic for the parent, not a protection.
///
/// Port of vigil_install.ps1 -Mode verify (#45), which stays the oracle in the CI e2e until the C#
/// installer replaces it (D22): same sections, same checks, same Pass/Warn/Fail levels.
/// </summary>
public sealed class Verify
{
    private readonly CountingReport _r;
    private readonly string? _childUserName;
    private readonly string _packageDir;
    private string? _serverUrl;

    public Verify(IReport report, string? childUserName, string packageDir)
    {
        _r = new CountingReport(report);
        _childUserName = childUserName;
        _packageDir = packageDir;
    }

    public int Pass => _r.Pass;
    public int Warn => _r.Warn;
    public int Fail => _r.Fail;

    public void Run()
    {
        if (WatchdogService.Exists() || Directory.Exists(VigilLayout.InstallDir)) RunInstalled();
        else RunRemoved();
    }

    // === Installed ===

    /// <summary>The guardian task still runs vigil_guardian.ps1: an installation from before #65. It works, so
    /// a warning (update replaces it), and its files are judged by that layout.</summary>
    private bool _oldGuardian;

    private void RunInstalled()
    {
        try
        {
            _oldGuardian = ScheduledTasks.Get(VigilLayout.GuardianTaskName) is { } task
                && task.Arguments.Contains(VigilLayout.GuardianScript, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception) { _oldGuardian = false; }

        // Each section on its own: an unexpected error is one failed check, not the end of the report.
        Section(Strings.SectionService(VigilLayout.ServiceName), CheckService);
        Section(Strings.SectionFiles(VigilLayout.InstallDir), CheckFiles);
        Section(Strings.SectionTasks, CheckTasks);
        Section(Strings.SectionPolicies, CheckPolicies);
        Section(Strings.SectionRuntime, CheckRuntime);
    }

    private void Section(string title, Action body)
    {
        _r.Section(title);
        try { body(); }
        catch (Exception ex) { _r.Check(CheckLevel.Fail, Strings.CheckCrashed(ex.Message)); }
    }

    private void CheckService()
    {
        var svc = WatchdogService.Read();
        _r.Check(svc is not null, Strings.ServiceRegistered);
        if (svc is null) return;

        _r.Check(svc.Status == ServiceControllerStatus.Running, Strings.ServiceStatus(svc.Status.ToString()));
        var automatic = svc.Start == 2 && !svc.DelayedAutoStart;
        _r.Check(automatic, Strings.ServiceStart(automatic ? "Automatic" : $"Start={svc.Start}{(svc.DelayedAutoStart ? " (delayed)" : "")}"));

        // Quoted since #47; older installs have it unquoted, which only matters with a space in the path.
        var exe = VigilLayout.PathOf(VigilLayout.WatchdogExe);
        var path = svc.ImagePath;
        var quoted = path.StartsWith('"');
        _r.Check(path.Trim('"').Equals(exe, StringComparison.OrdinalIgnoreCase) && (quoted || !exe.Contains(' ')),
            Strings.ServicePath(path, unquoted: path.Length > 0 && !quoted));

        _r.Check(svc.RecoveryAsInstalled, Strings.ServiceRecovery(svc.FailureActionsOnNonCrash));

        // DACL: what matters is that admins have no stop (WP) and no delete (SD).
        var sddl = WatchdogService.ReadSddl();
        var ba = Regex.Match(sddl, @"\(A;;([A-Z]+);;;BA\)") is { Success: true } m ? m.Groups[1].Value : null;
        _r.Check(ba is not null && !ba.Contains("WP") && !ba.Contains("SD"), Strings.ServiceDacl(ba));
    }

    private void CheckFiles()
    {
        string P(string name) => VigilLayout.PathOf(name);
        var watchdog = P(VigilLayout.WatchdogExe);
        // What the guardian task runs: vigil_setup.exe, or before #65 the PowerShell script.
        var guardianFile = _oldGuardian ? VigilLayout.GuardianScript : VigilLayout.SetupExe;
        var binaries = new[] { VigilLayout.WatchdogExe, VigilLayout.UiExe, guardianFile };
        var files = new[]
        {
            watchdog, P(VigilLayout.UiExe), P(guardianFile), P(VigilLayout.ConfigFile),
            P(VigilLayout.SigningFile), P(VigilLayout.GuardianFile), VigilLayout.BackupWatchdogPath,
        };
        // Relative to the folder: "backup\vigil_watchdog.exe", not just the name it shares with the original.
        var missing = files.Where(f => !File.Exists(f)).Select(f => Path.GetRelativePath(VigilLayout.InstallDir, f)).ToList();
        _r.Check(missing.Count == 0, Strings.FilesComplete(missing));

        var legacy = VigilLayout.LegacyPaths
            .Where(p => !(_oldGuardian && p.Equals(P(VigilLayout.GuardianScript), StringComparison.OrdinalIgnoreCase)))
            .Where(p => File.Exists(p) || Directory.Exists(p)).ToList();
        _r.Check(legacy.Count == 0, Strings.NoLegacyFiles(legacy));

        if (File.Exists(watchdog))
        {
            var hash = Sha256(watchdog);
            if (File.Exists(VigilLayout.BackupWatchdogPath))
                _r.Check(Sha256(VigilLayout.BackupWatchdogPath) == hash, Strings.BackupMatches);
            var guardianDat = P(VigilLayout.GuardianFile);
            if (File.Exists(guardianDat))
            {
                try
                {
                    var reference = Encoding.UTF8.GetString(Unprotect(guardianDat, null)).Trim();
                    _r.Check(reference.Equals(hash, StringComparison.OrdinalIgnoreCase), Strings.GuardianHashMatches);
                }
                catch (CryptographicException ex) { _r.Check(CheckLevel.Fail, Strings.DoesNotDecrypt(VigilLayout.GuardianFile, ex.Message)); }
            }
            // The package this program came from: is it the one installed?
            var pkg = Path.Combine(_packageDir, VigilLayout.WatchdogExe);
            if (File.Exists(pkg) && !Path.GetFullPath(pkg).Equals(Path.GetFullPath(watchdog), StringComparison.OrdinalIgnoreCase))
            {
                var same = binaries.Count(name =>
                    File.Exists(Path.Combine(_packageDir, name)) && File.Exists(P(name))
                    && Sha256(Path.Combine(_packageDir, name)) == Sha256(P(name)));
                _r.Check(same == binaries.Length, Strings.PackageMatches(_packageDir), CheckLevel.Warn);
            }
        }

        var signing = P(VigilLayout.SigningFile);
        if (File.Exists(signing))
        {
            try
            {
                var key = Unprotect(signing, Encoding.UTF8.GetBytes(VigilLayout.SigningKeyEntropy));
                _r.Check(key.Length >= 16, Strings.SigningKeyDecrypted(key.Length));
                Array.Clear(key);
            }
            catch (CryptographicException ex) { _r.Check(CheckLevel.Fail, Strings.DoesNotDecrypt(VigilLayout.SigningFile, ex.Message)); }
        }

        var toml = P(VigilLayout.ConfigFile);
        if (File.Exists(toml))
        {
            var text = File.ReadAllText(toml);
            _serverUrl = Regex.Match(text, "server_url\\s*=\\s*\"([^\"]+)\"") is { Success: true } m ? m.Groups[1].Value : null;
            _r.Check(_serverUrl is not null, Strings.ConfigServerUrl(_serverUrl));
            _r.Check(!text.Contains("bearer_token"), Strings.ConfigNoBearer);
        }

        if (Directory.Exists(VigilLayout.InstallDir)) CheckAcls();
    }

    private void CheckAcls()
    {
        var dir = new DirectoryInfo(VigilLayout.InstallDir).GetAccessControl();
        var dirOk = dir.AreAccessRulesProtected & FileAcls.HasExactRules(dir, FileAcls.FolderRules, out var dirDiff);
        _r.Check(dirOk, Strings.FolderAcl(dirOk ? null : $"protected={dir.AreAccessRulesProtected} {dirDiff}"));

        var wrong = new List<string>();
        var all = new DirectoryInfo(VigilLayout.InstallDir).EnumerateFileSystemInfos("*",
            new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = 0 });
        foreach (var item in all)
        {
            FileSystemSecurity sec = item is FileInfo f ? (FileSystemSecurity)f.GetAccessControl() : ((DirectoryInfo)item).GetAccessControl();
            if (item is FileInfo && VigilLayout.SecretFiles.Contains(item.Name, StringComparer.OrdinalIgnoreCase))
            {
                if (!(sec.AreAccessRulesProtected & FileAcls.HasExactRules(sec, FileAcls.SecretRules, out var secretDiff)))
                    wrong.Add($"{item.Name} ({secretDiff})");
            }
            else if (sec.AreAccessRulesProtected | !FileAcls.HasExactRules(sec, Array.Empty<AclRule>(), out var explicitDiff))
            {
                wrong.Add($"{item.Name} (explicit: {explicitDiff})");
            }
        }
        _r.Check(wrong.Count == 0, Strings.FilesAcl(wrong));
    }

    private void CheckTasks()
    {
        var task = ScheduledTasks.Get(VigilLayout.GuardianTaskName);
        _r.Check(task is not null, Strings.TaskExists(VigilLayout.GuardianTaskName));
        if (task is not null)
        {
            _r.Check(task.UserId is "S-1-5-18" or "SYSTEM" && task.RunLevel == "HighestAvailable", Strings.TaskAsSystem);
            _r.Check(task.Enabled, Strings.TaskEnabled(task.Enabled));
            _r.Check(task.HasBootTrigger && task.RepeatsEvery("PT5M"), Strings.TaskTriggers);
            if (_oldGuardian)
                _r.Check(CheckLevel.Warn, Strings.TaskOldGuardian(VigilLayout.PathOf(VigilLayout.GuardianScript)));
            else
            {
                var exe = VigilLayout.PathOf(VigilLayout.SetupExe);
                _r.Check(task.Command.Trim('"').Equals(exe, StringComparison.OrdinalIgnoreCase)
                         && task.Arguments.Trim() == Installer.GuardianArgument,
                    Strings.TaskRuns($"{exe} {Installer.GuardianArgument}"));
            }
            if (task.LastResult == TaskInfo.HasNotRun)
                _r.Check(CheckLevel.Pass, Strings.TaskNotRunYet);
            else if (task.LastResult is { } result)
                _r.Check(result == 0, Strings.TaskLastResult(result, task.LastRunTime), CheckLevel.Warn);
        }
        _r.Check(!ScheduledTasks.Exists(VigilLayout.LegacyTrayTaskName), Strings.NoLegacyTrayTask(VigilLayout.LegacyTrayTaskName));
        CheckNoTempTasks();

        foreach (var key in VigilLayout.SafeBootKeys)
            _r.Check(SafeBoot.Present(key), Strings.SafeBoot(SafeBootMode(key)));

        var (_, bcd) = NativeTool.Run("bcdedit.exe", "/enum", "{current}");
        var policy = Regex.Match(bcd, @"^bootmenupolicy\s+(\S+)", RegexOptions.Multiline) is { Success: true } m ? m.Groups[1].Value : "";
        _r.Check(policy == "Standard", Strings.BootMenu(policy), CheckLevel.Warn);
    }

    private void CheckNoTempTasks()
    {
        var left = ScheduledTasks.AllNames()
            .Where(n => VigilLayout.TempTaskPrefixes.Any(p => n.StartsWith(p, StringComparison.OrdinalIgnoreCase))).ToList();
        _r.Check(left.Count == 0, Strings.NoTempTasks(left));
    }

    private static string SafeBootMode(string key) => key.Split('\\')[^2];

    private void CheckPolicies()
    {
        var child = ChildAccountChoice.Resolve(_childUserName);
        if (child.Account is { } acc)
        {
            _r.Info(Strings.ChildAccount(acc.Name));
            CheckUserPolicies(Strings.AccountLabel(acc.Name), acc.Sid, acc.HiveFile, removed: false);
        }
        else
        {
            _r.Check(CheckLevel.Warn, Strings.NoChildAccountForVerify(child.Problem));
        }
        CheckUserPolicies(Strings.DefaultProfile, null, VigilLayout.DefaultProfileHive, removed: false);
    }

    /// <summary>Installed: every value set, no legacy one. Removed: nothing of ours left.</summary>
    private void CheckUserPolicies(string label, string? sid, string? hiveFile, bool removed)
    {
        List<PolicyValue> rows;
        using (var hive = UserHive.Open(sid, hiveFile))
        {
            if (hive is null) { _r.Check(CheckLevel.Warn, Strings.HiveUnreadable(label)); return; }
            rows = UserPolicies.Read(hive.Root);
        }
        var bad = removed
            ? rows.Where(v => v.State is PolicyState.Set or PolicyState.Legacy).ToList()
            : rows.Where(v => v.IsLegacy ? v.State == PolicyState.Legacy : v.State != PolicyState.Set).ToList();
        if (bad.Count == 0)
            _r.Check(CheckLevel.Pass, removed ? Strings.PoliciesRemoved(label) : Strings.PoliciesSet(label, VigilLayout.UserPolicies.Length));
        else
            _r.Check(CheckLevel.Fail, $"{label}: {string.Join(", ", bad.Select(v => $"{v.Policy.Label}={v.State.ToString().ToLowerInvariant()}"))}");
    }

    private void CheckRuntime()
    {
        // Someone is logged on (explorer runs) ⇒ the watchdog must have started vigil_ui in that session.
        if (Process.GetProcessesByName("explorer").Length > 0)
            _r.Check(Process.GetProcessesByName("vigil_ui").Length > 0, Strings.UiRunning);
        else
            _r.Info(Strings.NobodyLoggedOn);

        if (_serverUrl is not null)
            _r.Check(ServerReachable(_serverUrl), Strings.ServerResponds(_serverUrl));

        var (count, last) = RecentWatchdogProblems();
        _r.Check(count == 0, Strings.WatchdogEventLog(count, last), CheckLevel.Warn);
    }

    // === Removed ===

    private void RunRemoved()
    {
        _r.Section(Strings.SectionRemoved);
        _r.Check(!WatchdogService.Exists(), Strings.NoService(VigilLayout.ServiceName));
        _r.Check(!Directory.Exists(VigilLayout.InstallDir), Strings.NoFolder(VigilLayout.InstallDir));
        foreach (var name in new[] { VigilLayout.GuardianTaskName, VigilLayout.LegacyTrayTaskName })
            _r.Check(!ScheduledTasks.Exists(name), Strings.NoTask(name));
        CheckNoTempTasks();
        foreach (var key in VigilLayout.SafeBootKeys)
        {
            using var k = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(key);
            _r.Check(k is null, Strings.NoSafeBoot(SafeBootMode(key)));
        }
        var procs = VigilLayout.ProcessNames.Where(n => Process.GetProcessesByName(n).Length > 0).ToList();
        _r.Check(procs.Count == 0, Strings.NoProcesses(procs));
        // Harmless when left (an uninstall before #66 kept them), so a warning.
        var sources = VigilLayout.EventSources.Where(EventLog.SourceExists).ToList();
        _r.Check(sources.Count == 0, Strings.NoEventSources(sources), CheckLevel.Warn);
        foreach (var acc in Accounts.Interactive())
            CheckUserPolicies(Strings.AccountLabel(acc.Name), acc.Sid, acc.HiveFile, removed: true);
        CheckUserPolicies(Strings.DefaultProfile, null, VigilLayout.DefaultProfileHive, removed: true);
    }

    // === Helpers ===

    private static string Sha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    private static byte[] Unprotect(string path, byte[]? entropy) =>
        ProtectedData.Unprotect(File.ReadAllBytes(path), entropy, DataProtectionScope.LocalMachine);

    private static bool ServerReachable(string url)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(6) };
            return http.GetAsync(url.TrimEnd('/') + "/health").GetAwaiter().GetResult().IsSuccessStatusCode;
        }
        catch (Exception) { return false; }
    }

    /// <summary>The message without the "Category: …" / "EventId: …" lines Microsoft.Extensions.Logging puts first.</summary>
    private static string? FirstMessageLine(string? description) =>
        description?.Split('\n').Select(l => l.Trim())
            .FirstOrDefault(l => l.Length > 0 && !l.StartsWith("Category:", StringComparison.Ordinal)
                                 && !l.StartsWith("EventId:", StringComparison.Ordinal));

    /// <summary>Warnings and errors of the watchdog in the last 15 minutes, and the newest one's first line.</summary>
    private static (int Count, string? Last) RecentWatchdogProblems()
    {
        var query = new EventLogQuery("Application", PathType.LogName,
            $"*[System[Provider[@Name='{VigilLayout.WatchdogEventSource}'] and (Level=1 or Level=2 or Level=3) " +
            "and TimeCreated[timediff(@SystemTime) <= 900000]]]") { ReverseDirection = true };
        var count = 0;
        string? last = null;
        try
        {
            using var reader = new EventLogReader(query);
            for (var e = reader.ReadEvent(); e is not null; e = reader.ReadEvent())
            {
                using (e)
                {
                    count++;
                    if (last is null)
                    {
                        try { last = FirstMessageLine(e.FormatDescription()); }
                        catch (EventLogException) { last = $"event {e.Id}"; }
                    }
                }
            }
        }
        catch (EventLogException) { }  // no such source yet
        return (count, last);
    }
}
