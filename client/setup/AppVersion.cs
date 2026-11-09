using System.Diagnostics;
using System.Reflection;

namespace Vigil.Setup;

/// <summary>
/// Versions of the package (this program) and of the installation. Set by the build (build-package.ps1
/// -Version: the tag in a release, 0.0.0-ci.&lt;run&gt; in CI, client/Directory.Build.props 0.0.0-dev locally);
/// binaries built before that (v0.7 and older) carry the SDK default 1.0.0.
/// </summary>
public static class AppVersion
{
    /// <summary>This program's version, without the commit hash the SDK appends ("0.8.0+abc…").</summary>
    public static string Display => Clean(Assembly.GetExecutingAssembly()
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion);

    /// <summary>Version of an exe from its version resource, or null when it cannot be read.</summary>
    public static string? Of(string exe)
    {
        try { return File.Exists(exe) ? Clean(FileVersionInfo.GetVersionInfo(exe).ProductVersion) : null; }
        catch (Exception) { return null; }
    }

    /// <summary>The SDK default: a build from before versioned packages.</summary>
    public static bool IsUnversioned(string? version) => version is null or "1.0.0" or "1.0.0.0";

    /// <summary>
    /// Semantic version order: 0.8.0 &gt; 0.8.0-ci.12 &gt; 0.8.0-ci.9 &gt; 0.7.1. Pre-release identifiers compare
    /// numerically when both are numbers, otherwise ordinally. Unparsable versions compare as equal.
    /// </summary>
    public static int Compare(string a, string b)
    {
        var (coreA, preA) = Split(a);
        var (coreB, preB) = Split(b);
        if (!Version.TryParse(coreA, out var va) || !Version.TryParse(coreB, out var vb)) return 0;
        var c = va.CompareTo(vb);
        if (c != 0) return c;
        if (preA.Length == 0 || preB.Length == 0) return preB.Length.CompareTo(preA.Length);  // release > pre-release
        for (var i = 0; i < Math.Min(preA.Length, preB.Length); i++)
        {
            c = long.TryParse(preA[i], out var na) && long.TryParse(preB[i], out var nb)
                ? na.CompareTo(nb)
                : string.CompareOrdinal(preA[i], preB[i]);
            if (c != 0) return Math.Sign(c);
        }
        return preA.Length.CompareTo(preB.Length);
    }

    private static (string Core, string[] Pre) Split(string v)
    {
        v = Clean(v);
        var dash = v.IndexOf('-');
        return dash < 0 ? (v, Array.Empty<string>()) : (v[..dash], v[(dash + 1)..].Split('.'));
    }

    private static string Clean(string? v)
    {
        if (string.IsNullOrEmpty(v)) return "?";
        var plus = v.IndexOf('+');
        return plus >= 0 ? v[..plus] : v;
    }
}
