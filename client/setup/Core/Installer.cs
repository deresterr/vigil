using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32;
using Vigil.Shared;

namespace Vigil.Setup.Core;

/// <summary>What an install or update applies; built by the wizard or from the parameters.</summary>
public sealed record InstallRequest
{
    public required string ServerUrl { get; init; }
    /// <summary>The key from pairing; null = keep the installed signing.dat (update).</summary>
    public string? SigningKey { get; init; }
    /// <summary>The account the per-user policies go to; null = the Default profile only.</summary>
    public UserAccount? Child { get; init; }
    public bool ApplyUserPolicies { get; init; } = true;
    public bool DisableWinRE { get; init; }
}

/// <summary>
/// install / update / update-config / uninstall: port of vigil_install.ps1 (same files, values, service,
/// tasks and policies; verify of both installers must pass on the result). Each step reports a short title
/// for the parent (IReport.Section) and its technical details (Check/Info).
/// </summary>
public sealed class Installer
{
    private readonly CountingReport _r;
    private readonly string _sourceDir;

    public Installer(IReport report, string sourceDir)
    {
        _r = new CountingReport(report);
        _sourceDir = sourceDir;
    }

    /// <summary>Warnings reported so far (the wizard shows them in its summary).</summary>
    public int Warnings => _r.Warn;

    private static string P(string name) => VigilLayout.PathOf(name);

    /// <summary>Installed files that come from the package.</summary>
    public static readonly string[] PackageFiles = { VigilLayout.WatchdogExe, VigilLayout.UiExe, VigilLayout.GuardianScript };

    public static bool IsInstalled() => ServiceControl.Status() is not null || Directory.Exists(VigilLayout.InstallDir);

    // === Pairing (before anything changes) ===

    public string Pair(string serverUrl, string code)
    {
        _r.Section(Strings.StepPairing);
        var key = Pairing.Pair(serverUrl, code);
        _r.Check(CheckLevel.Pass, Strings.Paired(serverUrl));
        return key;
    }

    // === install / update ===

    /// <summary>
    /// Everything from scratch on top of whatever is there: the service is stopped and registered again,
    /// files copied, configuration written (or kept, update), protection, policies, ACLs, start.
    /// </summary>
    public void Install(InstallRequest req)
    {
        CheckSourceFiles();
        if (req.SigningKey is null) RequireInstalledKey();

        _r.Section(Strings.StepStopping);
        if (ServiceControl.Status() is not null)
        {
            ServiceControl.Stop(_r);
            if (!ServiceControl.Delete(_r)) throw new SetupException(Strings.ServiceDeleteFailed);
            _r.Check(CheckLevel.Pass, Strings.OldServiceRemoved);
        }
        StopUiProcesses();

        _r.Section(Strings.StepFiles);
        Directory.CreateDirectory(VigilLayout.InstallDir);
        InstallAcls.Reclaim(VigilLayout.InstallDir);
        Directory.CreateDirectory(Path.Combine(VigilLayout.InstallDir, VigilLayout.BackupDir));
        foreach (var name in PackageFiles)
        {
            File.Copy(Path.Combine(_sourceDir, name), P(name), overwrite: true);
            _r.Check(CheckLevel.Pass, name);
        }
        File.Copy(P(VigilLayout.WatchdogExe), VigilLayout.BackupWatchdogPath, overwrite: true);
        _r.Check(CheckLevel.Pass, $@"{VigilLayout.BackupDir}\{VigilLayout.WatchdogExe}");
        RemoveLegacyFiles();

        _r.Section(Strings.StepConfig);
        WriteGuardianHash();
        WriteConfig(req.ServerUrl);
        if (req.SigningKey is not null) WriteSigningKey(req.SigningKey);
        else _r.Info(Strings.KeyKept);

        _r.Section(Strings.StepService);
        ServiceControl.Create(_r);
        ServiceControl.RestrictDacl(_r);

        _r.Section(Strings.StepProtection);
        AddSafeBoot();
        ScheduledTasks.Create(VigilLayout.GuardianTaskName, ScheduledTasks.GuardianTaskXml("powershell.exe",
            $"-NoProfile -WindowStyle Hidden -ExecutionPolicy Bypass -File \"{P(VigilLayout.GuardianScript)}\""));
        _r.Check(CheckLevel.Pass, Strings.GuardianTaskCreated(VigilLayout.GuardianTaskName));
        RemoveTask(VigilLayout.LegacyTrayTaskName);
        DisableBootMenu();
        if (req.DisableWinRE) DisableWinRE();

        _r.Section(Strings.StepPolicies);
        if (req.ApplyUserPolicies) ApplyUserPolicies(req.Child);
        else _r.Info(Strings.PoliciesSkipped);

        _r.Section(Strings.StepAcls);
        InstallAcls.LockDown(VigilLayout.InstallDir);
        _r.Check(CheckLevel.Pass, Strings.AclsSet);

        _r.Section(Strings.StepStart);
        StartAndCheck();
    }

    /// <summary>The installed server address (vigil.toml), or null.</summary>
    public static string? InstalledServerUrl()
    {
        try
        {
            var text = File.ReadAllText(P(VigilLayout.ConfigFile));
            var m = System.Text.RegularExpressions.Regex.Match(text, "server_url\\s*=\\s*\"([^\"]+)\"");
            return m.Success ? m.Groups[1].Value : null;
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    // === update-config ===

    /// <summary>A new server address, a new key (re-pairing), or both; binaries and the rest stay.</summary>
    public void UpdateConfig(string serverUrl, string? signingKey)
    {
        if (!Directory.Exists(VigilLayout.InstallDir) || ServiceControl.Status() is null)
            throw new SetupException(Strings.NotInstalled);

        _r.Section(Strings.StepStopping);
        ServiceControl.Stop(_r);

        _r.Section(Strings.StepConfig);
        InstallAcls.Reclaim(VigilLayout.InstallDir);
        try
        {
            // Always rewritten: drops bearer_token of protocol v0 installs.
            WriteConfig(serverUrl);
            if (signingKey is not null) WriteSigningKey(signingKey);
            RemoveLegacyFiles();
        }
        finally
        {
            InstallAcls.LockDown(VigilLayout.InstallDir);
        }

        _r.Section(Strings.StepStart);
        StartAndCheck();
    }

    // === uninstall ===

    /// <summary>
    /// Best effort: every step runs even when one before it failed (also on a half-removed install, e.g. the
    /// folder deleted from WinRE); false when something is left.
    /// </summary>
    public bool Uninstall()
    {
        var failsBefore = _r.Fail;
        void Step(string title, Action body)
        {
            _r.Section(title);
            try { body(); }
            catch (Exception ex) { _r.Check(CheckLevel.Fail, ex.Message); }
        }

        Step(Strings.StepStopping, () =>
        {
            if (ServiceControl.Status() is null) { _r.Info(Strings.ServiceAbsent); return; }
            ServiceControl.Stop(_r);
            if (!ServiceControl.Delete(_r)) throw new SetupException(Strings.ServiceDeleteFailedReboot);
            _r.Check(CheckLevel.Pass, Strings.ServiceRemoved);
        });
        Step(Strings.StepProcesses, StopUiProcesses);
        Step(Strings.StepTasks, () =>
        {
            RemoveTask(VigilLayout.GuardianTaskName);
            RemoveTask(VigilLayout.LegacyTrayTaskName);
            foreach (var name in ScheduledTasks.AllNames().Where(n =>
                         VigilLayout.TempTaskPrefixes.Any(p => n.StartsWith(p, StringComparison.OrdinalIgnoreCase))))
                RemoveTask(name);
            foreach (var key in VigilLayout.SafeBootKeys)
                Registry.LocalMachine.DeleteSubKeyTree(key, throwOnMissingSubKey: false);
            _r.Check(CheckLevel.Pass, Strings.SafeBootRemoved);
        });
        Step(Strings.StepPolicies, ClearUserPolicies);
        Step(Strings.StepFilesRemove, () =>
        {
            if (!Directory.Exists(VigilLayout.InstallDir)) { _r.Info(Strings.NoFolder(VigilLayout.InstallDir)); return; }
            InstallAcls.DeleteTree(VigilLayout.InstallDir);
            _r.Check(CheckLevel.Pass, Strings.FolderRemoved(VigilLayout.InstallDir));
        });
        _r.Info(Strings.BootMenuNotRestored);
        return _r.Fail == failsBefore;
    }

    // === Steps ===

    private void CheckSourceFiles()
    {
        var missing = PackageFiles.Where(n => !File.Exists(Path.Combine(_sourceDir, n))).ToList();
        if (missing.Count > 0) throw new SetupException(Strings.SourceFilesMissing(_sourceDir, missing));
        if (Path.GetFullPath(_sourceDir).TrimEnd('\\').Equals(VigilLayout.InstallDir, StringComparison.OrdinalIgnoreCase))
            throw new SetupException(Strings.RunFromPackage);
    }

    private static void RequireInstalledKey()
    {
        var signing = P(VigilLayout.SigningFile);
        if (InstalledServerUrl() is null || !File.Exists(signing))
            throw new SetupException(Strings.NothingToUpdate);
    }

    /// <summary>vigil_ui (and the lock screen and tray of versions before D8) hold their exe open.
    /// An elevated installer may kill them despite their process DACL (BA full).</summary>
    private void StopUiProcesses()
    {
        foreach (var name in new[] { "vigil_ui", "vigil_lockscreen", "vigil_tray", "vigil_watchdog" })
        foreach (var p in Process.GetProcessesByName(name))
        {
            using (p)
            {
                try
                {
                    p.Kill();
                    p.WaitForExit(5000);
                    _r.Check(CheckLevel.Pass, Strings.ProcessStopped(name, p.Id));
                }
                catch (Exception ex) { _r.Check(CheckLevel.Warn, Strings.ProcessStopFailed(name, ex.Message)); }
            }
        }
    }

    /// <summary>Before v0.5: emergency.dat and a forgeable bypass.flag (#9); before D8: two exes (#35).</summary>
    private void RemoveLegacyFiles()
    {
        foreach (var path in VigilLayout.LegacyPaths)
        {
            try
            {
                if (File.Exists(path)) File.Delete(path);
                else if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
                else continue;
                _r.Check(CheckLevel.Pass, Strings.LegacyRemoved(path));
            }
            catch (Exception ex) { _r.Check(CheckLevel.Warn, Strings.LegacyRemoveFailed(path, ex.Message)); }
        }
    }

    /// <summary>The guardian's reference: SHA-256 hex of vigil_watchdog.exe, DPAPI LocalMachine.</summary>
    private void WriteGuardianHash()
    {
        using var stream = File.OpenRead(P(VigilLayout.WatchdogExe));
        var hash = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
        File.WriteAllBytes(P(VigilLayout.GuardianFile),
            ProtectedData.Protect(Encoding.UTF8.GetBytes(hash), null, DataProtectionScope.LocalMachine));
        _r.Check(CheckLevel.Pass, Strings.GuardianHashWritten(hash[..16]));
    }

    private void WriteConfig(string serverUrl)
    {
        File.WriteAllText(P(VigilLayout.ConfigFile),
            "# Vigil config. Brak sekretów: klucz podpisu jest w signing.dat (DPAPI, tylko SYSTEM).\r\n\r\n" +
            $"server_url = \"{serverUrl}\"\r\n", new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        _r.Check(CheckLevel.Pass, Strings.ConfigWritten(serverUrl));
    }

    private void WriteSigningKey(string base64Url)
    {
        var key = Pairing.DecodeKey(base64Url);
        try
        {
            File.WriteAllBytes(P(VigilLayout.SigningFile), ProtectedData.Protect(key,
                Encoding.UTF8.GetBytes(VigilLayout.SigningKeyEntropy), DataProtectionScope.LocalMachine));
        }
        finally { Array.Clear(key); }
        _r.Check(CheckLevel.Pass, Strings.SigningKeyWritten);
    }

    private void AddSafeBoot()
    {
        foreach (var key in VigilLayout.SafeBootKeys)
        {
            using var k = Registry.LocalMachine.CreateSubKey(key, writable: true);
            k.SetValue("", "Service");
        }
        _r.Check(CheckLevel.Pass, Strings.SafeBootAdded);
    }

    private void RemoveTask(string name)
    {
        if (!ScheduledTasks.Exists(name)) return;
        if (ScheduledTasks.Delete(name)) _r.Check(CheckLevel.Pass, Strings.TaskRemoved(name));
        else _r.Check(CheckLevel.Warn, Strings.TaskRemoveFailed(name));
    }

    private void DisableBootMenu()
    {
        var (rc, output) = NativeTool.Run("bcdedit.exe", "/set", "{current}", "bootmenupolicy", "Standard");
        if (rc == 0) _r.Check(CheckLevel.Pass, Strings.BootMenuDisabled);
        else _r.Check(CheckLevel.Warn, Strings.ToolFailed("bcdedit", rc, output.Trim()));
    }

    private void DisableWinRE()
    {
        var (rc, output) = NativeTool.Run("reagentc.exe", "/disable");
        if (rc == 0) _r.Check(CheckLevel.Pass, Strings.WinReDisabled);
        else _r.Check(CheckLevel.Warn, Strings.ToolFailed("reagentc", rc, output.Trim()));
    }

    /// <summary>The child's account (live hive when logged on, else its NTUSER.DAT) and the Default profile
    /// (accounts created later).</summary>
    private void ApplyUserPolicies(UserAccount? child)
    {
        if (child is null) _r.Check(CheckLevel.Warn, Strings.NoChildDefaultOnly);
        else ApplyTo(Strings.AccountLabel(child.Name), child.Sid, child.HiveFile);
        ApplyTo(Strings.DefaultProfile, null, VigilLayout.DefaultProfileHive);
    }

    private void ApplyTo(string label, string? sid, string? hiveFile)
    {
        try
        {
            using var hive = UserHive.Open(sid, hiveFile, writable: true);
            if (hive is null) { _r.Check(CheckLevel.Warn, Strings.HiveUnreadable(label)); return; }
            UserPolicies.Apply(hive.Root);
            _r.Check(CheckLevel.Pass, Strings.PoliciesApplied(label, hive.IsLive));
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or System.Security.SecurityException)
        {
            _r.Check(CheckLevel.Warn, $"{label}: {ex.Message}");
        }
    }

    /// <summary>Every loaded user hive, the Default profile and every account that is not logged on: whichever
    /// got the policies, also when an older install was told a different account.</summary>
    private void ClearUserPolicies()
    {
        var loaded = UserHive.LoadedUserSids();
        foreach (var sid in loaded) ClearIn(Strings.AccountLabel(Accounts.NameOf(sid)), sid, null);
        ClearIn(Strings.DefaultProfile, null, VigilLayout.DefaultProfileHive);
        foreach (var acc in Accounts.Interactive().Where(a => !loaded.Contains(a.Sid)))
            ClearIn(Strings.AccountLabel(acc.Name), null, acc.HiveFile);
    }

    private void ClearIn(string label, string? sid, string? hiveFile)
    {
        try
        {
            using var hive = UserHive.Open(sid, hiveFile, writable: true);
            if (hive is null) { _r.Check(CheckLevel.Warn, Strings.HiveUnreadable(label)); return; }
            UserPolicies.Clear(hive.Root);
            _r.Check(CheckLevel.Pass, Strings.PoliciesRemoved(label));
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or System.Security.SecurityException)
        {
            _r.Check(CheckLevel.Fail, $"{label}: {ex.Message}");
        }
    }

    /// <summary>Start, then a few seconds to catch a crash at start (bad config, missing file).</summary>
    private void StartAndCheck()
    {
        if (!ServiceControl.Start(_r)) throw new SetupException(Strings.ServiceDidNotStart);
        Thread.Sleep(5000);
        if (ServiceControl.Status() != System.ServiceProcess.ServiceControllerStatus.Running)
            throw new SetupException(Strings.ServiceStoppedAfterStart);
        _r.Check(CheckLevel.Pass, Strings.ServiceRunning);
    }
}
