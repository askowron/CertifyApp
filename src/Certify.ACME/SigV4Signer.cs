using System.Security.Cryptography;
using System.Text;

namespace Certify.ACME;

/// <summary>
/// Podpis SigV4 (AWS) dla Route53 REST API. Bez SDK - czysty HttpClient.
/// Testowany cross-checkiem z niezalezna implementacja (python/hmac).
/// </summary>
public static class SigV4Signer
{
    public const string Service = "route53";
    public const string Region = "us-east-1"; // Route53: globalny endpoint
    public const string Endpoint = "https://route53.amazonaws.com";

    public static string Sha256Hex(string s)
    {
        using var sha = SHA256.Create();
        return Convert.ToHexString(sha.ComputeHash(Encoding.UTF8.GetBytes(s))).ToLowerInvariant();
    }

    public static string Sha256Hex(byte[] b)
    {
        using var sha = SHA256.Create();
        return Convert.ToHexString(sha.ComputeHash(b)).ToLowerInvariant();
    }

    /// <summary>URI-encode jak wymaga SigV4 (RFC 3986, bez '/'-owania w path segmentach poza separatorami).</summary>
    public static string Encode(string s, bool encodeSlash)
    {
        var sb = new StringBuilder();
        foreach (var b in Encoding.UTF8.GetBytes(s))
        {
            var c = (char)b;
            if ((c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '_' || c == '-' || c == '~' || c == '.' || (!encodeSlash && c == '/'))
                sb.Append(c);
            else
                sb.Append('%').Append(b.ToString("X2"));
        }
        return sb.ToString();
    }

    public static string CanonicalQueryString(SortedDictionary<string, string> query)
    {
        var sb = new StringBuilder();
        var first = true;
        foreach (var kv in query)
        {
            if (!first) sb.Append('&');
            first = false;
            sb.Append(Encode(kv.Key, true)).Append('=').Append(Encode(kv.Value, true));
        }
        return sb.ToString();
    }

    public static string BuildCanonicalRequest(string method, string path, string canonicalQuery, string host, string amzDate, string payloadHash)
    {
        var headers = $"host:{host}\nx-amz-date:{amzDate}\n";
        const string signedHeaders = "host;x-amz-date";
        return $"{method}\n{EncodePath(path)}\n{canonicalQuery}\n{headers}\n{signedHeaders}\n{payloadHash}";
    }

    public static string EncodePath(string path) =>
        string.Join("/", path.Split('/').Select(s => Encode(s, true)));

    public static string CredentialScope(string dateStamp) =>
        $"{dateStamp}/{Region}/{Service}/aws4_request";

    public static string StringToSign(string amzDate, string scope, string canonicalRequest) =>
        $"AWS4-HMAC-SHA256\n{amzDate}\n{scope}\n{Sha256Hex(canonicalRequest)}";

    public static byte[] SigningKey(string secretKey, string dateStamp)
    {
        using var h1 = new HMACSHA256(Encoding.UTF8.GetBytes("AWS4" + secretKey));
        var kDate = h1.ComputeHash(Encoding.UTF8.GetBytes(dateStamp));
        using var h2 = new HMACSHA256(kDate);
        var kRegion = h2.ComputeHash(Encoding.UTF8.GetBytes(Region));
        using var h3 = new HMACSHA256(kRegion);
        var kService = h3.ComputeHash(Encoding.UTF8.GetBytes(Service));
        using var h4 = new HMACSHA256(kService);
        return h4.ComputeHash(Encoding.UTF8.GetBytes("aws4_request"));
    }

    public static string Signature(byte[] signingKey, string stringToSign)
    {
        using var h = new HMACSHA256(signingKey);
        return Convert.ToHexString(h.ComputeHash(Encoding.UTF8.GetBytes(stringToSign))).ToLowerInvariant();
    }

    /// <summary>Pelny naglowek Authorization. amzDate format: yyyyMMddTHHmmssZ.</summary>
    public static string AuthorizationHeader(
        string method, string path, SortedDictionary<string, string> query,
        string host, string payload, string accessKey, string secretKey, string amzDate, string dateStamp)
    {
        var payloadHash = Sha256Hex(payload);
        var canonical = BuildCanonicalRequest(method, path, CanonicalQueryString(query), host, amzDate, payloadHash);
        var scope = CredentialScope(dateStamp);
        var sts = StringToSign(amzDate, scope, canonical);
        var sig = Signature(SigningKey(secretKey, dateStamp), sts);
        return $"AWS4-HMAC-SHA256 Credential={accessKey}/{scope}, SignedHeaders=host;x-amz-date, Signature={sig}";
    }
}
