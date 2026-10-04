using System.Security.Cryptography.X509Certificates;
using Certify.Core.Localization;

namespace Certify.Core.Services;

/// <summary>
/// Zapis artefaktow certyfikatu w ukladzie certs/&lt;id&gt;/ :
/// &lt;domain&gt;.pfx, &lt;domain&gt;.crt (leaf), &lt;domain&gt;.key, chain.pem.
/// Ustawia sciezki, daty i status na modelu. Wspoldzielony przez flow
/// Certes (DNS) i raw ACME (IP), zeby deployery dzialaly tak samo.
/// </summary>
public static class CertificateArtifactWriter
{
    public const string PfxPassword = "certify";

    public static async Task WriteAsync(
        string dataDirectory,
        Models.ManagedCertificate cert,
        string leafPem,
        string chainPem,
        string keyPem,
        byte[] pfxBytes,
        bool isRenewal,
        IProgress<string>? log = null,
        CancellationToken ct = default)
    {
        var certDir = Path.Combine(dataDirectory, "certs", cert.Id);
        Directory.CreateDirectory(certDir);
        var baseName = SafeFileName(cert.PrimaryDomain);
        var pfxPath = Path.Combine(certDir, $"{baseName}.pfx");
        var certPemPath = Path.Combine(certDir, $"{baseName}.crt");
        var keyPemPath = Path.Combine(certDir, $"{baseName}.key");

        await File.WriteAllBytesAsync(pfxPath, pfxBytes, ct);
        await File.WriteAllTextAsync(certPemPath, leafPem, ct);
        await File.WriteAllTextAsync(keyPemPath, keyPem, ct);
        await File.WriteAllTextAsync(Path.Combine(certDir, "chain.pem"), chainPem, ct);

        log?.Report($"[ACME] Zapisano PFX: {pfxPath}");

        using var x509 = new X509Certificate2(pfxBytes, PfxPassword);
        cert.DateExpiry = x509.NotAfter.ToUniversalTime();
        cert.DateRenewed = DateTime.UtcNow;
        // Kasuj termin ponowienia po bledzie - nastepne odnowienie wg daty wygasniecia.
        cert.DateNextRenewalAttempt = null;
        cert.PfxPath = pfxPath;
        cert.CertPath = certPemPath;
        cert.KeyPath = keyPemPath;
        cert.PfxPassword = PfxPassword;
        cert.Status = Models.CertificateStatus.Valid;
        cert.StatusMessage = isRenewal ? UIStrings.T("Status_Renewed") : UIStrings.T("Status_Issued");
    }

    /// <summary>
    /// Nazwa pliku z domeny: "*.example.com" -> "_wildcard.example.com",
    /// IPv6 (dwukropki) i inne znaki niedozwolone w Windows -> "_".
    /// </summary>
    public static string SafeFileName(string domain)
    {
        var name = domain.Trim();
        if (name.StartsWith("*.")) name = "_wildcard" + name[1..];
        var invalid = Path.GetInvalidFileNameChars().Concat([':', '*', '?']).ToHashSet();
        name = new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray());
        return string.IsNullOrWhiteSpace(name) ? "certificate" : name;
    }

    /// <summary>Buduje PFX (leaf + klucz + intermediates) czysc .NET.</summary>
    public static byte[] BuildPfx(string leafPem, string chainPem, System.Security.Cryptography.ECDsa certKey, string password)
    {
        var leaf = X509Certificate2.CreateFromPem(leafPem);
        using var leafWithKey = leaf.CopyWithPrivateKey(certKey);
        var collection = new X509Certificate2Collection { leafWithKey };
        var leafNorm = Normalize(leafPem);
        foreach (var block in SplitPemBlocks(chainPem))
        {
            if (Normalize(block) == leafNorm) continue;
            collection.Add(X509Certificate2.CreateFromPem(block));
        }
        return collection.Export(X509ContentType.Pfx, password)
            ?? throw new InvalidOperationException("Nie udalo sie zbudowac PFX.");
    }

    public static List<string> SplitPemBlocks(string pem) =>
        System.Text.RegularExpressions.Regex.Matches(pem, @"-----BEGIN CERTIFICATE-----.*?-----END CERTIFICATE-----",
            System.Text.RegularExpressions.RegexOptions.Singleline)
            .Select(m => m.Value.Trim()).ToList();

    private static string Normalize(string block) =>
        new(block.Where(c => !char.IsWhiteSpace(c)).ToArray());
}
