using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace Certify.ACME;

public class AcmeException(string type, string detail, int statusCode) : Exception($"{type}: {detail} (HTTP {statusCode})")
{
    public string Type { get; } = type;
    public string Detail { get; } = detail;
    public int StatusCode { get; } = statusCode;
    /// <summary>Retry-After z naglowka (limity CA). Ustawiane przez klienta.</summary>
    public TimeSpan? RetryAfter { get; set; }
}

/// <summary>
/// Minimalny klient ACME (RFC 8555): directory, nonce, podpisane POST.
/// Uzywany tylko dla orderow z identyfikatorami IP (RFC 8738),
/// ktorych Certes 3.0.4 nie wspiera (IdentifierType = Dns only).
/// </summary>
public class RawAcmeClient : IDisposable
{
    private readonly HttpClient _http;
    private string? _nonce;
    private bool _disposed;

    public RawAcmeClient(HttpClient? http = null)
    {
        _http = http ?? new HttpClient();
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("CertifyApp/1.0");
    }

    public void Dispose()
    {
        if (!_disposed) { _http.Dispose(); _disposed = true; }
        GC.SuppressFinalize(this);
    }

    public async Task<Dictionary<string, string>> GetDirectoryAsync(string directoryUrl, CancellationToken ct)
    {
        using var resp = await _http.GetAsync(directoryUrl, ct);
        resp.EnsureSuccessStatusCode();
        var json = await resp.Content.ReadAsStringAsync(ct);
        var dict = new Dictionary<string, string>();
        foreach (var prop in JsonDocument.Parse(json).RootElement.EnumerateObject())
            if (prop.Value.ValueKind == JsonValueKind.String)
                dict[prop.Name] = prop.Value.GetString()!;
        return dict;
    }

    public async Task RefreshNonceAsync(string newNonceUrl, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Head, newNonceUrl);
        using var resp = await _http.SendAsync(req, ct);
        _nonce = resp.Headers.TryGetValues("replay-nonce", out var v) ? v.FirstOrDefault() : null;
        if (string.IsNullOrEmpty(_nonce))
            throw new InvalidOperationException("Serwer ACME nie zwrocil replay-nonce.");
    }

    /// <summary>POST z JWS; payload "" = POST-as-GET. Zwraca (body, location).
    /// buildBody dostaje aktualny nonce (retry badNonce ze swiezym).</summary>
    public async Task<(string body, string? location)> PostSignedAsync(
        string url, Func<string, string> buildBody, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(_nonce))
            throw new InvalidOperationException("Brak nonce - najpierw RefreshNonceAsync.");
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var body = buildBody(_nonce!);
            using var content = new StringContent(body, Encoding.UTF8, "application/jose+json");
            using var resp = await _http.PostAsync(url, content, ct);
            _nonce = resp.Headers.TryGetValues("replay-nonce", out var v) ? v.FirstOrDefault() : _nonce;
            var respBody = await resp.Content.ReadAsStringAsync(ct);
            var location = resp.Headers.Location?.ToString();
            if ((int)resp.StatusCode is >= 200 and < 300)
                return (respBody, location);
            var (type, detail) = ParseProblem(respBody);
            if (type.EndsWith(":badNonce", StringComparison.Ordinal) && attempt == 0 && !string.IsNullOrEmpty(_nonce))
                continue; // odswiezony nonce z naglowka, retry
            var acmeEx = new AcmeException(type, detail, (int)resp.StatusCode)
            {
                RetryAfter = ParseRetryAfter(resp.Headers)
            };
            throw acmeEx;
        }
        throw new InvalidOperationException("Unreachable");
    }

    public void SetNonce(string nonce) => _nonce = nonce;

    /// <summary>Retry-After: sekundy albo data HTTP. Null gdy brak/nieprawidlowy.</summary>
    public static TimeSpan? ParseRetryAfter(System.Net.Http.Headers.HttpResponseHeaders headers)
    {
        try
        {
            if (!headers.TryGetValues("Retry-After", out var values)) return null;
            var v = values.FirstOrDefault()?.Trim();
            if (string.IsNullOrEmpty(v)) return null;
            if (int.TryParse(v, out var sec) && sec >= 0) return TimeSpan.FromSeconds(sec);
            if (DateTimeOffset.TryParse(v, out var date))
            {
                var diff = date - DateTimeOffset.UtcNow;
                return diff > TimeSpan.Zero ? diff : TimeSpan.Zero;
            }
        }
        catch { }
        return null;
    }

    private static (string type, string detail) ParseProblem(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            var r = doc.RootElement;
            var type = r.TryGetProperty("type", out var t) ? t.GetString() ?? "unknown" : "unknown";
            var detail = r.TryGetProperty("detail", out var d) ? d.GetString() ?? body : body;
            return (type, detail.Length > 300 ? detail[..300] : detail);
        }
        catch
        {
            return ("unknown", body.Length > 300 ? body[..300] : body);
        }
    }

    public static string? JsonString(string json, string prop)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String
                ? v.GetString() : null;
        }
        catch { return null; }
    }
}
