using System.Text.Json;

namespace Certify.ACME;

/// <summary>
/// Automatyczne EAB dla ZeroSSL z samego adresu email (jak Certify The Web / acme.sh):
/// POST https://api.zerossl.com/acme/eab-credentials-email, form email=...
/// Odpowiedz: {"success":true,"eab_kid":"...","eab_hmac_key":"..."}.
/// ZeroSSL zaklada (albo uzywa istniejacego) konto przypisane do emaila.
/// </summary>
public static class ZeroSslEab
{
    public const string EmailEndpoint = "https://api.zerossl.com/acme/eab-credentials-email";

    public static async Task<(string KeyId, string HmacKey)> FetchByEmailAsync(
        string email, CancellationToken ct, HttpClient? http = null)
    {
        var client = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        try
        {
            using var content = new FormUrlEncodedContent([new KeyValuePair<string, string>("email", email.Trim())]);
            using var resp = await client.PostAsync(EmailEndpoint, content, ct);
            var body = await resp.Content.ReadAsStringAsync(ct);
            if (!resp.IsSuccessStatusCode)
                throw new InvalidOperationException($"HTTP {(int)resp.StatusCode}: {Short(body)}");
            return ParseResponse(body);
        }
        finally
        {
            if (http == null) client.Dispose();
        }
    }

    // ---- czyste funkcje (testowalne) ----

    /// <summary>Wyciaga (kid, hmac) albo rzuca z opisem bledu ZeroSSL.</summary>
    public static (string KeyId, string HmacKey) ParseResponse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.TryGetProperty("success", out var s) && s.ValueKind == JsonValueKind.True
            && root.TryGetProperty("eab_kid", out var kid) && kid.ValueKind == JsonValueKind.String
            && root.TryGetProperty("eab_hmac_key", out var hmac) && hmac.ValueKind == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(kid.GetString()) && !string.IsNullOrWhiteSpace(hmac.GetString()))
            return (kid.GetString()!, hmac.GetString()!);

        // Blad: {"success":false,"error":{"code":2900,"type":"invalid_email"}}
        if (root.TryGetProperty("error", out var err))
        {
            var type = err.TryGetProperty("type", out var t) ? t.ToString() : null;
            var code = err.TryGetProperty("code", out var c) ? c.ToString() : null;
            throw new InvalidOperationException(type ?? code ?? Short(json));
        }
        throw new InvalidOperationException(Short(json));
    }

    private static string Short(string s) => s.Length > 200 ? s[..200] : s;
}
