using System.Security.Cryptography;
using System.Text;

namespace Certify.ACME;

/// <summary>
/// Minimalne JWS (RFC 7515) dla ACME: ES256 + EAB (HS256).
/// Czyste funkcje - testowalne bez sieci.
/// </summary>
public static class AcmeJws
{
    public static string B64U(byte[] b) =>
        Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static string B64U(string s) => B64U(Encoding.UTF8.GetBytes(s));

    public static byte[] FromB64U(string s)
    {
        s = s.Replace('-', '+').Replace('_', '/');
        switch (s.Length % 4)
        {
            case 2: s += "=="; break;
            case 3: s += "="; break;
        }
        return Convert.FromBase64String(s);
    }

    public static string Escape(string s) => s.Replace("\\", "\\\\").Replace("\"", "\\\"");

    /// <summary>Kanonicalny JWK (EC P-256) do naglowka / thumbprintu.</summary>
    public static string BuildEcJwk(ECParameters p) =>
        $"{{\"crv\":\"P-256\",\"kty\":\"EC\",\"x\":\"{B64U(p.Q.X!)}\",\"y\":\"{B64U(p.Q.Y!)}\"}}";

    /// <summary>Thumbprint RFC 7638 kanonicznego JWK JSON.</summary>
    public static string Thumbprint(string canonicalJwkJson)
    {
        using var sha = SHA256.Create();
        return B64U(sha.ComputeHash(Encoding.UTF8.GetBytes(canonicalJwkJson)));
    }

    public static string AccountThumbprint(ECDsa accountKey)
    {
        var p = accountKey.ExportParameters(false);
        return Thumbprint(BuildEcJwk(p));
    }

    /// <summary>Pelny obiekt JWS (flattened JSON) dla payloadu ("" = POST-as-GET).</summary>
    public static string EncodeJws(ECDsa accountKey, string protectedJson, string payloadJson)
    {
        var p64 = B64U(protectedJson);
        var pay64 = payloadJson.Length == 0 ? "" : B64U(payloadJson);
        var sig = accountKey.SignData(Encoding.ASCII.GetBytes(p64 + "." + pay64), HashAlgorithmName.SHA256);
        return $"{{\"protected\":\"{p64}\",\"payload\":\"{pay64}\",\"signature\":\"{B64U(sig)}\"}}";
    }

    /// <summary>Zewnetrzny protected dla EAB (podpisany kluczem HMAC z CA).</summary>
    public static string EncodeEab(string eabKeyId, string eabHmacB64U, string accountJwkJson, string newAccountUrl)
    {
        var prot = $"{{\"alg\":\"HS256\",\"kid\":\"{Escape(eabKeyId)}\",\"url\":\"{Escape(newAccountUrl)}\"}}";
        var p64 = B64U(prot);
        var pay64 = B64U(accountJwkJson);
        using var hmac = new HMACSHA256(FromB64U(eabHmacB64U));
        var sig = hmac.ComputeHash(Encoding.ASCII.GetBytes(p64 + "." + pay64));
        return $"{{\"protected\":\"{p64}\",\"payload\":\"{pay64}\",\"signature\":\"{B64U(sig)}\"}}";
    }

    public static string KeyAuthorization(string token, string accountThumbprint) =>
        $"{token}.{accountThumbprint}";
}
