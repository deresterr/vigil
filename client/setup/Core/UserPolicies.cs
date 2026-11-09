using System.Globalization;
using Microsoft.Win32;
using Vigil.Setup.Native;
using Vigil.Shared;

namespace Vigil.Setup.Core;

/// <summary>
/// A user's registry hive: the live one under HKEY_USERS when the user is logged on, otherwise the
/// NTUSER.DAT file loaded as an application hive (private to this process, unloaded on dispose).
/// No reg.exe: DisableRegistryTools, set by versions before v0.6, blocks it for the installer itself (D12).
/// </summary>
public sealed class UserHive : IDisposable
{
    public RegistryKey Root { get; }

    private UserHive(RegistryKey root) => Root = root;

    /// <returns>null when neither the live hive nor the file can be opened.</returns>
    public static UserHive? Open(string? sid, string? hiveFile)
    {
        if (sid is not null && Registry.Users.OpenSubKey(sid) is { } live) return new UserHive(live);
        if (hiveFile is null || !File.Exists(hiveFile)) return null;
        var rc = Win32.RegLoadAppKeyW(hiveFile, out var handle, Win32.KEY_READ, 0, 0);
        if (rc != 0) { handle.Dispose(); return null; }
        return new UserHive(RegistryKey.FromHandle(handle));
    }

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
    /// <summary>A value of an older version that is still there.</summary>
    Legacy,
}

public sealed record PolicyValue(UserPolicy Policy, PolicyState State, bool IsLegacy);

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
            result.Add(new PolicyValue(p, state, false));
        }
        foreach (var p in VigilLayout.LegacyUserPolicies)
            result.Add(new PolicyValue(p, ValueOf(root, p) is null ? PolicyState.Clear : PolicyState.Legacy, true));
        return result;
    }

    private static string? ValueOf(RegistryKey root, UserPolicy p)
    {
        using var key = root.OpenSubKey(p.Key);
        return Text(key?.GetValue(p.Name));
    }

    private static string? Text(object? v) => v is null ? null : Convert.ToString(v, CultureInfo.InvariantCulture);
}
