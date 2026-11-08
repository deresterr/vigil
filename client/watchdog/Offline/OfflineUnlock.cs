using System.Buffers.Binary;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using Vigil.Watchdog.Configuration;
using Vigil.Watchdog.Ui;
using Vigil.Watchdog.Server;

namespace Vigil.Watchdog.Offline;

/// <summary>
/// Unlock without the server (#9): the parent's TOTP code is verified here, against the secret
/// in totp.dat. Same rules as the server (server/app/endpoints/totp.py): ±1 time step, a step is
/// accepted once, every 3rd failure locks for 60 s / 300 s / 900 s (cap).
/// A successful code allows use for a limited time; the grant is reported to the server on
/// reconnect (journal) and ends as soon as the server answers again.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class OfflineUnlock
{
    public const int MaxGrantMinutes = 120;
    private const int FailuresPerLockout = 3;
    private static readonly int[] LockoutSteps = { 60, 300, 900 };

    private readonly TotpSecretStore _store;
    private readonly ILogger<OfflineUnlock> _logger;
    private readonly object _gate = new();
    private long _lastStep;
    private int _failures;
    private DateTime _lockedUntilUtc = DateTime.MinValue;
    private DateTime? _grantUntilUtc;
    private readonly List<OfflineGrantDto> _pending = new();

    public OfflineUnlock(TotpSecretStore store, ILogger<OfflineUnlock> logger)
    {
        _store = store;
        _logger = logger;
    }

    public bool HasSecret => _store.Secret is not null;

    public bool IsGrantActive
    {
        get { lock (_gate) return _grantUntilUtc is { } until && DateTime.UtcNow < until; }
    }

    public TotpResult TryUnlock(string code, int minutesRequested)
    {
        var secret = _store.Secret;
        if (secret is null)
            return new TotpResult(false, Reason: "offline_not_configured");

        lock (_gate)
        {
            var now = DateTime.UtcNow;
            if (now < _lockedUntilUtc)
                return new TotpResult(false, Reason: "lockout",
                                      LockoutSecondsRemaining: (int)Math.Ceiling((_lockedUntilUtc - now).TotalSeconds));

            var step = MatchStep(secret, code.Trim(), now);
            if (step is null || step.Value <= _lastStep)
            {
                _failures++;
                int? lockout = null;
                if (_failures % FailuresPerLockout == 0)
                {
                    var idx = Math.Min(_failures / FailuresPerLockout, LockoutSteps.Length) - 1;
                    lockout = LockoutSteps[idx];
                    _lockedUntilUtc = now.AddSeconds(lockout.Value);
                }
                _logger.LogWarning("Offline unlock: wrong code (failures={Failures})", _failures);
                return new TotpResult(false, Reason: "invalid_code", LockoutSecondsRemaining: lockout);
            }

            var minutes = Math.Clamp(minutesRequested, 1, MaxGrantMinutes);
            _lastStep = step.Value;
            _failures = 0;
            _grantUntilUtc = now.AddMinutes(minutes);
            _pending.Add(new OfflineGrantDto
            {
                AtUtc = now.ToString("yyyy-MM-ddTHH:mm:ss'Z'", System.Globalization.CultureInfo.InvariantCulture),
                Minutes = minutes,
                Step = step.Value,
            });
            _logger.LogInformation("Offline unlock granted for {Minutes} min", minutes);
            return new TotpResult(true, "offline_unlock", minutes);
        }
    }

    /// <summary>Grants not yet reported; send them with the next heartbeat.</summary>
    public List<OfflineGrantDto>? PendingGrants()
    {
        lock (_gate) return _pending.Count == 0 ? null : new List<OfflineGrantDto>(_pending);
    }

    /// <summary>Server answered: the reported grants are recorded there, the offline grant ends.</summary>
    public void OnServerReachable(List<OfflineGrantDto>? reported)
    {
        lock (_gate)
        {
            if (reported is not null) _pending.RemoveAll(reported.Contains);
            _grantUntilUtc = null;
        }
    }

    // RFC 6238 with pyotp defaults: SHA-1, 6 digits, 30 s, base32 secret.
    private static long? MatchStep(string base32Secret, string code, DateTime nowUtc)
    {
        if (code.Length != 6 || !code.All(char.IsAsciiDigit)) return null;
        var key = Base32Decode(base32Secret);
        var current = new DateTimeOffset(nowUtc).ToUnixTimeSeconds() / 30;
        for (var offset = -1; offset <= 1; offset++)
        {
            var step = current + offset;
            if (CryptographicOperations.FixedTimeEquals(
                    Encoding.ASCII.GetBytes(Hotp(key, step)), Encoding.ASCII.GetBytes(code)))
                return step;
        }
        return null;
    }

    private static string Hotp(byte[] key, long counter)
    {
        Span<byte> msg = stackalloc byte[8];
        BinaryPrimitives.WriteInt64BigEndian(msg, counter);
        var hash = HMACSHA1.HashData(key, msg);
        var o = hash[^1] & 0x0F;
        var bin = ((hash[o] & 0x7F) << 24) | (hash[o + 1] << 16) | (hash[o + 2] << 8) | hash[o + 3];
        return (bin % 1_000_000).ToString("D6", System.Globalization.CultureInfo.InvariantCulture);
    }

    private static byte[] Base32Decode(string s)
    {
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
        var clean = s.Trim().TrimEnd('=').ToUpperInvariant().Replace(" ", "");
        var bytes = new List<byte>(clean.Length * 5 / 8);
        int buffer = 0, bits = 0;
        foreach (var c in clean)
        {
            var v = alphabet.IndexOf(c);
            if (v < 0) throw new FormatException("invalid base32 character in TOTP secret");
            buffer = (buffer << 5) | v;
            bits += 5;
            if (bits >= 8)
            {
                bytes.Add((byte)(buffer >> (bits - 8)));
                bits -= 8;
            }
        }
        return bytes.ToArray();
    }
}
