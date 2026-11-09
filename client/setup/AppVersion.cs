using System.Diagnostics;
using System.Reflection;

namespace Vigil.Setup;

/// <summary>
/// Versions of the package (this program) and of the installation. Set by the build (build-package.ps1
/// -Version, from the tag in a release); binaries built before that carry the SDK default 1.0.0.
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

    private static string Clean(string? v)
    {
        if (string.IsNullOrEmpty(v)) return "?";
        var plus = v.IndexOf('+');
        return plus >= 0 ? v[..plus] : v;
    }
}
