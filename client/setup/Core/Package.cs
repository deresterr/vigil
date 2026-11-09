using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using Vigil.Shared;

namespace Vigil.Setup.Core;

/// <summary>
/// The client package is one file (#76): vigil_setup.exe with vigil_watchdog.exe and vigil_ui.exe appended
/// after its .NET bundle (the host finds the bundle through an offset in its header and ignores what follows).
/// Written by build-package.ps1, read only here:
///
///   [vigil_setup.exe as published][entry]...[entry][trailer]
///   entry:   int32 name length, name (UTF-8), int64 size, int64 stored length, SHA-256 of the file (32 B),
///            the file gzipped (stored length)
///   trailer: "VIGILPK1" (8 B), int64 offset of the first entry (= length of vigil_setup.exe), int32 entries
///
/// Little-endian. The installer puts only the first part in ProgramData: the guardian task runs a ~12 MB
/// vigil_setup.exe, and that copy (or a cut-off download) has no trailer, so no package.
/// </summary>
public sealed class Package
{
    private static readonly byte[] Magic = "VIGILPK1"u8.ToArray();
    private const int TrailerLength = 8 + 8 + 4;
    private const int MaxEntries = 16;

    private readonly Dictionary<string, Entry> _entries;
    private string? _setupSha256;

    /// <summary>The package file (the vigil_setup.exe that was started).</summary>
    public string Path { get; }

    /// <summary>Length of vigil_setup.exe itself: the payload starts here.</summary>
    public long SetupLength { get; }

    private sealed record Entry(long Size, long Offset, long StoredLength, string Sha256);

    private Package(string path, long setupLength, Dictionary<string, Entry> entries)
    {
        Path = path;
        SetupLength = setupLength;
        _entries = entries;
    }

    /// <summary>The package this program carries, or null (the installed copy, a cut-off download, a dev build).
    /// Throws SetupException when the trailer is there but the rest does not add up.</summary>
    public static Package? OfThisProgram() => Environment.ProcessPath is { } p ? Open(p) : null;

    public static Package? Open(string path)
    {
        try { return Read(path); }
        catch (EndOfStreamException) { throw Corrupt(path, "length"); }
    }

    private static Package? Read(string path)
    {
        using var f = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (f.Length < TrailerLength) return null;
        using var r = new BinaryReader(f, Encoding.UTF8, leaveOpen: true);
        f.Seek(-TrailerLength, SeekOrigin.End);
        if (!r.ReadBytes(Magic.Length).AsSpan().SequenceEqual(Magic)) return null;
        var payloadEnd = f.Length - TrailerLength;
        var offset = r.ReadInt64();
        var count = r.ReadInt32();
        if (offset <= 0 || offset >= payloadEnd || count is < 1 or > MaxEntries)
            throw Corrupt(path, "trailer");

        var entries = new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
        f.Seek(offset, SeekOrigin.Begin);
        for (var i = 0; i < count; i++)
        {
            var nameLength = r.ReadInt32();
            if (nameLength is < 1 or > 260) throw Corrupt(path, $"entry {i}");
            var name = Encoding.UTF8.GetString(r.ReadBytes(nameLength));
            var size = r.ReadInt64();
            var stored = r.ReadInt64();
            var sha256 = Convert.ToHexString(r.ReadBytes(32)).ToLowerInvariant();
            var at = f.Position;
            if (size < 0 || stored < 0 || at + stored > payloadEnd) throw Corrupt(path, name);
            entries[name] = new Entry(size, at, stored, sha256);
            f.Seek(stored, SeekOrigin.Current);
        }
        if (f.Position != payloadEnd) throw Corrupt(path, "length");
        return new Package(path, offset, entries);
    }

    public IEnumerable<string> Missing(IEnumerable<string> names) => names.Where(n => !_entries.ContainsKey(n));

    /// <summary>SHA-256 (lowercase hex) of a file as the package installs it; vigil_setup.exe = its first part.</summary>
    public string Sha256Of(string name)
    {
        if (name.Equals(VigilLayout.SetupExe, StringComparison.OrdinalIgnoreCase))
        {
            if (_setupSha256 is null)
            {
                using var f = new FileStream(Path, FileMode.Open, FileAccess.Read, FileShare.Read);
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                CopyRange(f, 0, SetupLength, hash.AppendData);
                _setupSha256 = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
            }
            return _setupSha256;
        }
        return Get(name).Sha256;
    }

    /// <summary>Writes a file of the package to <paramref name="target"/>: unpacked and checked against its
    /// SHA-256 before it replaces anything. vigil_setup.exe is the first part of the package.</summary>
    public void Extract(string name, string target)
    {
        var temp = target + ".new";
        try
        {
            using (var f = new FileStream(Path, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var output = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                if (name.Equals(VigilLayout.SetupExe, StringComparison.OrdinalIgnoreCase))
                {
                    CopyRange(f, 0, SetupLength, output.Write);
                }
                else
                {
                    var e = Get(name);
                    var stored = new byte[e.StoredLength];
                    f.Seek(e.Offset, SeekOrigin.Begin);
                    f.ReadExactly(stored);
                    using var gzip = new GZipStream(new MemoryStream(stored), CompressionMode.Decompress);
                    gzip.CopyTo(output);
                }
            }
            if (!Sha256File(temp).Equals(Sha256Of(name), StringComparison.Ordinal))
                throw Corrupt(Path, name);
            File.Move(temp, target, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
    }

    private Entry Get(string name) =>
        _entries.TryGetValue(name, out var e) ? e : throw new SetupException(Strings.SourceFilesMissing(Path, new[] { name }));

    private static void CopyRange(Stream from, long offset, long length, Action<byte[], int, int> to)
    {
        from.Seek(offset, SeekOrigin.Begin);
        var buffer = new byte[1 << 20];
        while (length > 0)
        {
            var n = from.Read(buffer, 0, (int)Math.Min(buffer.Length, length));
            if (n == 0) throw new EndOfStreamException();
            to(buffer, 0, n);
            length -= n;
        }
    }

    private static string Sha256File(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    private static SetupException Corrupt(string path, string what) => new(Strings.PackageCorrupt(path, what));
}
