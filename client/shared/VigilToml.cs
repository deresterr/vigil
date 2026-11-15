using System.Text;
using System.Text.RegularExpressions;

namespace Vigil.Shared;

/// <summary>
/// C:\ProgramData\Vigil\vigil.toml, written by vigil_setup only: comment lines and <c>key = "string"</c> lines,
/// read by the watchdog, the installer and verify. A TOML subset with no library behind it (#167): the file holds
/// two strings. Unknown keys are kept as read and ignored; a line outside the subset makes the file invalid, so the
/// service fails at start instead of running half-configured.
/// </summary>
public static class VigilToml
{
    public const string ServerUrl = "server_url";
    /// <summary>The child's account in the standard-account setup (D30, #160): the watchdog counts and locks only its
    /// sessions. Absent in the one-account setup: every session is the child's.</summary>
    public const string ChildSid = "child_sid";

    private static readonly Regex Pair = new(@"^\s*([A-Za-z_][A-Za-z0-9_-]*)\s*=\s*""((?:[^""\\]|\\[""\\])*)""\s*(?:#.*)?$");

    /// <exception cref="InvalidDataException">A line that is neither empty, a comment nor <c>key = "string"</c>.</exception>
    public static Dictionary<string, string> Parse(string text)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        var lines = text.Replace("\r\n", "\n").Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].TrimStart('\uFEFF');
            if (line.Trim().Length == 0 || line.TrimStart().StartsWith('#')) continue;
            var m = Pair.Match(line);
            if (!m.Success) throw new InvalidDataException($"vigil.toml line {i + 1} is not key = \"value\": {line.Trim()}");
            values[m.Groups[1].Value] = Regex.Replace(m.Groups[2].Value, @"\\([""\\])", "$1");
        }
        return values;
    }

    /// <summary>The value of <paramref name="key"/> in the file, or null (missing file, key or invalid file).</summary>
    public static string? Read(string path, string key)
    {
        try { return Parse(File.ReadAllText(path)).GetValueOrDefault(key); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException) { return null; }
    }

    public static string Write(string serverUrl, string? childSid)
    {
        var text = new StringBuilder()
            .Append("# Vigil configuration. No secrets: the signing key is in signing.dat (DPAPI, SYSTEM only).\r\n\r\n")
            .Append($"{ServerUrl} = \"{Escape(serverUrl)}\"\r\n");
        if (childSid is not null)
            text.Append("# The child's own account (standard): only its sessions are counted and locked.\r\n")
                .Append($"{ChildSid} = \"{Escape(childSid)}\"\r\n");
        return text.ToString();
    }

    private static string Escape(string s) => s.Replace("\\", "\\\\").Replace("\"", "\\\"");
}
