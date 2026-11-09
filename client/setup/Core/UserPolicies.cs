using System.Globalization;
using Microsoft.Win32;
using Vigil.Setup.Native;
using Vigil.Shared;

namespace Vigil.Setup.Core;

/// <summary>
/// A user's registry hive: the live one under HKEY_USERS when the user is logged on, otherwise the
/// NTUSER.DAT file loaded as an application hive (private to this process, unloaded on dispose).
/// Through the registry API (RegLoadAppKey), not reg.exe: no privilege or global mount name needed.
/// </summary>
public sealed class UserHive : IDisposable
{
    public RegistryKey Root { get; }

    private UserHive(RegistryKey root) => Root = root;

    /// <summary>True when the hive is the user's live one (HKEY_USERS\&lt;sid&gt;).</summary>
    public bool IsLive { get; private init; }

    /// <returns>null when neither the live hive nor the file can be opened.</returns>
    public static UserHive? Open(string? sid, string? hiveFile, bool writable = false)
    {
        if (sid is not null && Registry.Users.OpenSubKey(sid, writable) is { } live)
            return new UserHive(live) { IsLive = true };
        if (hiveFile is null || !File.Exists(hiveFile)) return null;
        var rc = Win32.RegLoadAppKeyW(hiveFile, out var handle, writable ? Win32.KEY_ALL_ACCESS : Win32.KEY_READ, 0, 0);
        if (rc != 0) { handle.Dispose(); return null; }
        return new UserHive(RegistryKey.FromHandle(handle));
    }

    /// <summary>SIDs of the loaded user hives (logged-on users), without the *_Classes hives.</summary>
    public static List<string> LoadedUserSids() =>
        Registry.Users.GetSubKeyNames()
            .Where(n => n.StartsWith("S-1-5-21-", StringComparison.Ordinal) && !n.EndsWith("_Classes", StringComparison.Ordinal))
            .ToList();

    public void Dispose() => Root.Dispose();
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
