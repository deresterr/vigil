using System.Globalization;
using Microsoft.Win32;
using Vigil.Setup.Native;
using Vigil.Shared;

namespace Vigil.Setup.Core;

/// <summary>
/// A user's registry hive: the live one under HKEY_USERS when the user is logged on, otherwise the NTUSER.DAT
/// file mounted under HKEY_USERS\Vigil_&lt;pid&gt;_&lt;n&gt; (RegLoadKey, which replays the hive's .LOG files: the
/// hive of an account that has signed in and out is usually left dirty, and RegLoadAppKey refused it with 1009)
/// and unloaded on dispose. Through the registry API, not reg.exe, which a policy may block.
/// </summary>
public sealed class UserHive : IDisposable
{
    private const string MountPrefix = "Vigil_";
    private static int _mounts;

    public RegistryKey Root { get; }
    private readonly string? _mount;

    private UserHive(RegistryKey root, string? mount = null)
    {
        Root = root;
        _mount = mount;
    }

    /// <summary>True when the hive is the user's live one (HKEY_USERS\&lt;sid&gt;).</summary>
    public bool IsLive { get; private init; }

    /// <returns>null when neither the live hive nor the file can be opened; <paramref name="error"/> then says why
    /// (a Win32 error: 2 = no hive file, 1314 = not elevated, else RegLoadKey's).</returns>
    public static UserHive? Open(string? sid, string? hiveFile, out int error, bool writable = false)
    {
        error = 0;
        if (sid is not null && Registry.Users.OpenSubKey(sid, writable) is { } live)
            return new UserHive(live) { IsLive = true };
        if (hiveFile is null || !File.Exists(hiveFile)) { error = 2; return null; }
        if (!Privileges.TryEnableHiveLoading()) { error = 1314; return null; }
        UnloadLeftovers();
        var mount = $"{MountPrefix}{Environment.ProcessId}_{Interlocked.Increment(ref _mounts)}";
        var rc = Win32.RegLoadKeyW(Win32.HKEY_USERS, mount, hiveFile);
        if (rc != 0) { error = rc; return null; }
        if (Registry.Users.OpenSubKey(mount, writable) is not { } root)
        {
            Win32.RegUnLoadKeyW(Win32.HKEY_USERS, mount);
            error = 5;
            return null;
        }
        return new UserHive(root, mount);
    }

    /// <summary>A mount left by a run that was killed holds the file: the user would get a temporary profile at the
    /// next sign-in. Process exit does not unload it, the next run does.</summary>
    private static void UnloadLeftovers()
    {
        var own = $"{MountPrefix}{Environment.ProcessId}_";
        foreach (var name in Registry.Users.GetSubKeyNames())
            if (name.StartsWith(MountPrefix, StringComparison.Ordinal) && !name.StartsWith(own, StringComparison.Ordinal))
                Win32.RegUnLoadKeyW(Win32.HKEY_USERS, name);
    }

    /// <summary>SIDs of the loaded user hives (logged-on users), without the *_Classes hives.</summary>
    public static List<string> LoadedUserSids() =>
        Registry.Users.GetSubKeyNames()
            .Where(n => n.StartsWith("S-1-5-21-", StringComparison.Ordinal) && !n.EndsWith("_Classes", StringComparison.Ordinal))
            .ToList();

    /// <summary>Every handle into a mounted hive must be closed first (the callers' subkeys are), or the unload
    /// fails and the file stays held until the next run.</summary>
    public void Dispose()
    {
        Root.Dispose();
        if (_mount is not null) Win32.RegUnLoadKeyW(Win32.HKEY_USERS, _mount);
    }
}

public enum PolicyState
{
    /// <summary>The installer's value.</summary>
    Set,
    /// <summary>Absent or the uninstall value.</summary>
    Clear,
    /// <summary>Something else.</summary>
    Other,
}

public sealed record PolicyValue(UserPolicy Policy, PolicyState State);

public static class UserPolicies
{
    public static List<PolicyValue> Read(RegistryKey root)
    {
        var result = new List<PolicyValue>();
        foreach (var p in VigilLayout.UserPolicies)
        {
            var v = ValueOf(root, p);
            var state = v is not null && v == Text(p.Value) ? PolicyState.Set
                : v is null || v == Text(p.Reset) ? PolicyState.Clear
                : PolicyState.Other;
            result.Add(new PolicyValue(p, state));
        }
        return result;
    }

    /// <summary>Sets every value.</summary>
    public static void Apply(RegistryKey root)
    {
        foreach (var p in VigilLayout.UserPolicies)
        {
            using var key = root.CreateSubKey(p.Key, writable: true);
            key.SetValue(p.Name, p.Value, p.Kind);
        }
    }

    /// <summary>Each value back to its reset value, or removed.</summary>
    public static void Clear(RegistryKey root)
    {
        foreach (var p in VigilLayout.UserPolicies)
        {
            using var key = root.OpenSubKey(p.Key, writable: true);
            if (key is null) continue;
            if (p.Reset is null) key.DeleteValue(p.Name, throwOnMissingValue: false);
            else key.SetValue(p.Name, p.Reset, p.Kind);
        }
    }

    private static string? ValueOf(RegistryKey root, UserPolicy p)
    {
        using var key = root.OpenSubKey(p.Key);
        return Text(key?.GetValue(p.Name));
    }

    private static string? Text(object? v) => v is null ? null : Convert.ToString(v, CultureInfo.InvariantCulture);
}
