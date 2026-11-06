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
/// Response weryfikuje X-Signature = HMAC-SHA256(signing_key, nonce + body).
/// Jeśli weryfikacja nie przejdzie → rzuca SignatureMismatchException, watchdog traktuje to jak unreachable.
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
        _http = new HttpClient
        {
            BaseAddress = new Uri(config.ServerUrl + "/"),
            // Krótszy timeout niż domyślne 100s — heartbeat ma być szybki, dłużej nie ma sensu
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

    public async Task SendGoodbyeAsync(GoodbyeRequest req, CancellationToken ct)
    {
        await PostSignedAsync<GoodbyeResponse>("v1/goodbye", req, ct).ConfigureAwait(false);
    }

    public async Task<PauseToggleResponse> SendPauseToggleAsync(CancellationToken ct)
    {
        var (response, _) = await PostSignedAsync<PauseToggleResponse>(
            "v1/pause_toggle", new { }, ct).ConfigureAwait(false);
        return response;
    }

    /// <summary>
    /// Wspólna ścieżka dla wszystkich endpointów /v1/*. Buduje nonce/timestamp, wysyła,
    /// weryfikuje X-Signature, deserializuje body.
    /// </summary>
    private async Task<(T result, string nonce)> PostSignedAsync<T>(
        string path, object body, CancellationToken ct) where T : class
    {
        // Nonce: 16 bajtów losowych, base64 urlsafe bez paddingu (zgodnie z formatem servera)
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

        // Spec: 401/429 mają sensowne body (np. {"ok": false, "reason": "invalid_code"}).
        // Te response też są podpisywane przez server (auth przeszło, dopiero biznesowo failed).
        // 401 BEFORE auth (np. duplicate_nonce) — nie ma podpisu, traktujemy jako error.
        if (!resp.IsSuccessStatusCode && resp.StatusCode != System.Net.HttpStatusCode.Unauthorized
            && resp.StatusCode != (System.Net.HttpStatusCode)429)
        {
            var bodyStr = Encoding.UTF8.GetString(responseBytes);
            throw new HttpRequestException(
                $"{path} zwrócił {(int)resp.StatusCode} {resp.ReasonPhrase}: {bodyStr}");
        }

        // Zarówno 200 jak i 401/429 PO auth są podpisywane. Brak podpisu = serwer odrzucił przed auth.
        var signatureHeader = resp.Headers.TryGetValues("X-Signature", out var sigVals)
            ? sigVals.FirstOrDefault()
            : null;

        if (signatureHeader is null)
        {
            // Brak podpisu = pre-auth rejection (zły podpis, zły timestamp, duplicate nonce).
            // To NIE jest odpowiedź której powinniśmy ufać.
            var bodyStr = Encoding.UTF8.GetString(responseBytes);
            throw new HttpRequestException(
                $"{path} zwrócił {(int)resp.StatusCode} bez X-Signature (pre-auth rejection): {bodyStr}");
        }

        // Weryfikuj HMAC: signing_key, message = nonce_bytes + body_bytes
        if (!VerifySignature(nonce, responseBytes, signatureHeader))
        {
            throw new SignatureMismatchException(
                $"{path}: X-Signature nie matchuje. Możliwy MITM lub błąd po stronie servera.");
        }

        var deserialized = JsonSerializer.Deserialize<T>(responseBytes, _json)
            ?? throw new InvalidDataException($"{path}: deserializacja JSON dała null.");

        return (deserialized, nonce);
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

        // Constant-time compare, żeby uniknąć timing attacks. Strings tej samej długości,
        // więc CryptographicOperations.FixedTimeEquals się sprawdza.
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

public sealed class SignatureMismatchException : Exception
{
    public SignatureMismatchException(string message) : base(message) { }
}
