using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Vigil.Setup.Core;

/// <summary>The server: its address as the user types it, /health, and pairing (/v1/pair).</summary>
public static class Pairing
{
    /// <summary>"192.168.1.5" → "http://192.168.1.5:8088"; a scheme and a port the user gives are kept.</summary>
    public static string NormalizeUrl(string raw)
    {
        var u = raw.Trim();
        if (!Regex.IsMatch(u, "^https?://", RegexOptions.IgnoreCase)) u = "http://" + u;
        u = u.TrimEnd('/');
        if (Regex.IsMatch(u, "^https?://[^/:]+$", RegexOptions.IgnoreCase)) u += ":8088";
        return u;
    }

    public static bool IsValidUrl(string normalized) =>
        Uri.TryCreate(normalized, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" && uri.Host.Length > 0;

    /// <summary>
    /// The way the watchdog talks to the server (HeartbeatClient): no redirects — one turned the pairing POST into
    /// a GET (405) behind a proxy that forces HTTPS — no system proxy (the server is on the LAN or a VPN, and the
    /// service runs as SYSTEM with other proxy settings than this user), small responses (#132).
    /// </summary>
    private static HttpClient NewClient(TimeSpan timeout) =>
        new(new SocketsHttpHandler { AllowAutoRedirect = false, UseProxy = false })
        {
            Timeout = timeout,
            MaxResponseContentBufferSize = 64 * 1024,
        };

    public static bool IsHealthy(string serverUrl) => Check(serverUrl).Ok;

    /// <summary>/health answers 200; or where the server sends us instead (the address to type), or neither.</summary>
    public static (bool Ok, string? RedirectTo) Check(string serverUrl)
    {
        try
        {
            using var http = NewClient(TimeSpan.FromSeconds(6));
            using var resp = http.GetAsync(serverUrl + "/health").GetAwaiter().GetResult();
            if (resp.StatusCode == HttpStatusCode.OK) return (true, null);
            if ((int)resp.StatusCode is >= 300 and < 400 && resp.Headers.Location is { } to)
            {
                var target = (to.IsAbsoluteUri ? to : new Uri(new Uri(serverUrl + "/"), to)).ToString();
                return (false, Regex.Replace(target, "/health/?$", "", RegexOptions.IgnoreCase).TrimEnd('/'));
            }
            return (false, null);
        }
        catch (Exception) { return (false, null); }
    }

    /// <summary>"1234 5678", "1234-5678" → "12345678"; null unless 8 digits.</summary>
    public static string? NormalizeCode(string raw)
    {
        var digits = new string(raw.Where(char.IsAsciiDigit).ToArray());
        return digits.Length == 8 ? digits : null;
    }

    /// <summary>
    /// Uses the code: the server creates the device (or gives a re-paired one a new key, which disables the
    /// old key at once) and returns its signing key, base64url.
    /// </summary>
    public static string Pair(string serverUrl, string code)
    {
        HttpResponseMessage resp;
        using var http = NewClient(TimeSpan.FromSeconds(10));
        try
        {
            resp = http.PostAsync(serverUrl + "/v1/pair",
                    new StringContent($"{{\"code\":\"{code}\"}}", System.Text.Encoding.UTF8, "application/json"))
                .GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            throw new PairingException(PairingFailure.Server, Strings.ServerUnreachable(serverUrl, ex.Message));
        }
        using (resp)
        {
            var body = resp.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            switch (resp.StatusCode)
            {
                case HttpStatusCode.OK:
                    var key = Field(body, "signing_key");
                    if (string.IsNullOrEmpty(key)) break;
                    return key;
                case HttpStatusCode.NotFound:
                    throw new PairingException(PairingFailure.NoCode, Strings.PairingNoCode);
                case HttpStatusCode.Unauthorized:
                    var left = int.TryParse(Field(body, "attempts_left"), out var n) ? n : (int?)null;
                    throw new PairingException(PairingFailure.WrongCode, Strings.PairingWrongCode(left), left);
            }
            throw new PairingException(PairingFailure.Server, Strings.PairingFailed((int)resp.StatusCode));
        }
    }

    private static string? Field(string json, string name)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.TryGetProperty(name, out var v) ? v.ToString() : null;
        }
        catch (JsonException) { return null; }
    }

    /// <summary>base64url without padding (the server's format) to bytes.</summary>
    public static byte[] DecodeKey(string base64Url)
    {
        var s = base64Url.Replace('-', '+').Replace('_', '/');
        s += (s.Length % 4) switch { 2 => "==", 3 => "=", _ => "" };
        return Convert.FromBase64String(s);
    }
}
