using System.Globalization;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Vigil.Watchdog.Configuration;

namespace Vigil.Watchdog.Server;

/// <summary>
/// HTTP client for the server (protocol v2, see server/app/auth.py). Every request carries:
/// - X-Timestamp: unix ms (UTC)
/// - X-Nonce: 16 random bytes, base64url without padding
/// - X-Signature: base64(HMAC-SHA256(signing_key, "POST\n/v1/...\n" + ts + "\n" + nonce + "\n" + hex(sha256(body))))
///
/// Responses carry X-Signature = HMAC-SHA256(signing_key, nonce + body); a missing or wrong one throws, and the
/// watchdog treats it like an unreachable server.
/// </summary>
public sealed class HeartbeatClient : IDisposable
{
    private readonly HttpClient _http;
    private readonly VigilConfig _config;
    private readonly byte[] _signingKey;
    private readonly ILogger<HeartbeatClient> _logger;

    private static readonly JsonSerializerOptions _json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public HeartbeatClient(VigilConfig config, byte[] signingKey, ILogger<HeartbeatClient> logger)
    {
        _config = config;
        _signingKey = signingKey;
        _logger = logger;
        // Drop idle pooled connections well before the server does (uvicorn --timeout-keep-alive 75).
        // Otherwise a request sometimes reuses a connection the server is just closing -> 10054 reset.
        var handler = new SocketsHttpHandler { PooledConnectionIdleTimeout = TimeSpan.FromSeconds(30) };
        _http = new HttpClient(handler)
        {
            BaseAddress = new Uri(config.ServerUrl + "/"),
            // Not the default 100 s: a slow heartbeat is as good as none
            Timeout = TimeSpan.FromSeconds(10),
        };
    }

    public async Task<HeartbeatResponse> SendHeartbeatAsync(
        HeartbeatRequest req, CancellationToken ct)
    {
        var (response, _) = await PostSignedAsync<HeartbeatResponse>(
            "v1/heartbeat", req, ct).ConfigureAwait(false);
        return response;
    }

    public async Task<TotpAttemptResponse> SendTotpAttemptAsync(
        TotpAttemptRequest req, CancellationToken ct)
    {
        var (response, _) = await PostSignedAsync<TotpAttemptResponse>(
            "v1/totp_attempt", req, ct).ConfigureAwait(false);
        return response;
    }

    public async Task SendTamperEventAsync(TamperEventRequest req, CancellationToken ct)
    {
        await PostSignedAsync<GoodbyeResponse>("v1/tamper_event", req, ct).ConfigureAwait(false);
    }

    public async Task SendGuardianRepairAsync(GuardianRepairRequest req, CancellationToken ct)
    {
        await PostSignedAsync<GoodbyeResponse>("v1/tamper_event", req, ct).ConfigureAwait(false);
    }

    public async Task SendGoodbyeAsync(GoodbyeRequest req, CancellationToken ct)
    {
        await PostSignedAsync<GoodbyeResponse>("v1/goodbye", req, ct).ConfigureAwait(false);
    }

    /// <param name="paused">The state wanted ("I'm back": false), or null to toggle (the tray icon).</param>
    public async Task<PauseToggleResponse> SendPauseToggleAsync(bool? paused, CancellationToken ct)
    {
        var (response, _) = await PostSignedAsync<PauseToggleResponse>(
            "v1/pause_toggle", new PauseToggleRequest { Paused = paused }, ct).ConfigureAwait(false);
        return response;
    }

    /// <summary>
    /// Every /v1/* call: nonce and timestamp, signed request, response signature checked, body deserialized.
    /// </summary>
    private async Task<(T result, string nonce)> PostSignedAsync<T>(
        string path, object body, CancellationToken ct) where T : class
    {
        // Nonce: 16 random bytes, base64url without padding (the server's format)
        var nonceBytes = RandomNumberGenerator.GetBytes(16);
        var nonce = Base64UrlEncode(nonceBytes);

        var ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture);

        // Sign the exact bytes that go on the wire.
        var bodyBytes = JsonSerializer.SerializeToUtf8Bytes(body, body.GetType(), _json);
        var content = new ByteArrayContent(bodyBytes);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };

        using var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = content,
        };
        request.Headers.Add("X-Nonce", nonce);
        request.Headers.Add("X-Timestamp", ts);
        request.Headers.Add("X-Signature", SignRequest("POST", "/" + path, ts, nonce, bodyBytes));

        using var resp = await _http.SendAsync(request, ct).ConfigureAwait(false);
        var responseBytes = await resp.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);

        // 401/429 after authentication carry a meaningful, signed body (e.g. {"ok": false, "reason": "invalid_code"}).
        // A 401 before authentication (e.g. a reused nonce) is unsigned and handled below as an error.
        if (!resp.IsSuccessStatusCode && resp.StatusCode != System.Net.HttpStatusCode.Unauthorized
            && resp.StatusCode != (System.Net.HttpStatusCode)429)
        {
            var bodyStr = Encoding.UTF8.GetString(responseBytes);
            throw new HttpRequestException(
                $"{path} returned {(int)resp.StatusCode} {resp.ReasonPhrase}: {bodyStr}");
        }

        // 200 and 401/429 after authentication are signed; no signature = rejected before authentication.
        var signatureHeader = resp.Headers.TryGetValues("X-Signature", out var sigVals)
            ? sigVals.FirstOrDefault()
            : null;

        if (signatureHeader is null)
        {
            // invalid_signature: the server no longer knows this key (re-paired or removed in the panel).
            if (resp.StatusCode == System.Net.HttpStatusCode.Unauthorized && IsInvalidSignature(responseBytes))
                throw new KeyRejectedException($"{path}: the server rejected this computer's key (invalid_signature)");
            // Rejected before authentication (bad signature, timestamp, reused nonce): nothing to trust.
            var bodyStr = Encoding.UTF8.GetString(responseBytes);
            throw new HttpRequestException(
                $"{path} returned {(int)resp.StatusCode} without X-Signature (rejected before authentication): {bodyStr}");
        }

        // HMAC with signing_key over nonce + body
        if (!VerifySignature(nonce, responseBytes, signatureHeader))
        {
            throw new SignatureMismatchException(
                $"{path}: X-Signature does not match (a man in the middle, or a server error)");
        }

        var deserialized = JsonSerializer.Deserialize<T>(responseBytes, _json)
            ?? throw new InvalidDataException($"{path}: the JSON body deserialized to null");

        return (deserialized, nonce);
    }

    /// <summary>The body of a pre-auth 401 is FastAPI's {"detail": "..."} (server/app/auth.py _reject).</summary>
    private static bool IsInvalidSignature(byte[] body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            return doc.RootElement.TryGetProperty("detail", out var d)
                && d.ValueKind == JsonValueKind.String && d.GetString() == "invalid_signature";
        }
        catch (JsonException) { return false; }
    }

    /// <summary>Request signature; must match canonical_request() in server/app/auth.py.</summary>
    private string SignRequest(string method, string path, string ts, string nonce, byte[] body)
    {
        var bodyHash = Convert.ToHexString(SHA256.HashData(body)).ToLowerInvariant();
        var canonical = Encoding.UTF8.GetBytes($"{method}\n{path}\n{ts}\n{nonce}\n{bodyHash}");
        return Convert.ToBase64String(HMACSHA256.HashData(_signingKey, canonical));
    }

    private bool VerifySignature(string nonce, byte[] responseBody, string expectedB64)
    {
        var nonceBytes = Encoding.ASCII.GetBytes(nonce);
        var message = new byte[nonceBytes.Length + responseBody.Length];
        Buffer.BlockCopy(nonceBytes, 0, message, 0, nonceBytes.Length);
        Buffer.BlockCopy(responseBody, 0, message, nonceBytes.Length, responseBody.Length);

        var actualBytes = HMACSHA256.HashData(_signingKey, message);
        var actualB64 = Convert.ToBase64String(actualBytes);

        // Constant-time comparison.
        var expectedBytes = Convert.FromBase64String(expectedB64);
        return CryptographicOperations.FixedTimeEquals(actualBytes, expectedBytes);
    }

    private static string Base64UrlEncode(byte[] bytes)
    {
        return Convert.ToBase64String(bytes)
            .Replace('+', '-')
            .Replace('/', '_')
            .TrimEnd('=');
    }

    public void Dispose() => _http.Dispose();
}

/// <summary>
/// The server refused this computer's key (401 invalid_signature): it was re-paired or removed in the panel.
/// An HttpRequestException like any other network failure, so heartbeats stay fail-closed exactly as before.
/// </summary>
public sealed class KeyRejectedException(string message) : HttpRequestException(message);

public sealed class SignatureMismatchException : Exception
{
    public SignatureMismatchException(string message) : base(message) { }
}
