namespace Certify.Core.Models;

/// <summary>
/// Katalog urzedow CA (jak w CTW: wybor CA per certyfikat).
/// URL-e zweryfikowane z dokumentacja CA (ZeroSSL/Buypass/Google/lego, 2026).
/// ZeroSSL nie ma publicznego staging -> brak odpowiednika Staging.
/// </summary>
public record CaOption(string Display, CertificateAuthority Value);

public record CaInfo(
    string Display,
    string? DirectoryUrl,
    bool RequiresEab,
    bool IsStaging,
    CertificateAuthority? StagingCounterpart,
    string? EabHelp);

public static class CertificateAuthorityCatalog
{
    public static readonly IReadOnlyList<CaOption> All =
    [
        new("Let's Encrypt", CertificateAuthority.LetsEncrypt),
        new("Let's Encrypt (Staging)", CertificateAuthority.LetsEncryptStaging),
        new("ZeroSSL", CertificateAuthority.ZeroSsl),
        new("Google Public CA", CertificateAuthority.Google),
        new("Google Public CA (Staging)", CertificateAuthority.GoogleStaging),
        new("Custom ACME server", CertificateAuthority.CustomAcme)
    ];

    private static readonly Dictionary<CertificateAuthority, CaInfo> Infos = new()
    {
        [CertificateAuthority.LetsEncrypt] = new(
            "Let's Encrypt", "https://acme-v02.api.letsencrypt.org/directory",
            false, false, CertificateAuthority.LetsEncryptStaging, null),
        [CertificateAuthority.LetsEncryptStaging] = new(
            "Let's Encrypt (Staging)", "https://acme-staging-v02.api.letsencrypt.org/directory",
            false, true, null, null),
        [CertificateAuthority.ZeroSsl] = new(
            "ZeroSSL", "https://acme.zerossl.com/v2/DV90",
            true, false, null,
            "ZeroSSL wymaga EAB. Zostaw puste - zostanie pobrany automatycznie dla adresu email (jak w Certify The Web). " +
            "Mozesz tez wpisac EAB KID + HMAC z panelu ZeroSSL (Developer), zeby certyfikaty trafialy na Twoje konto."),
        [CertificateAuthority.Google] = new(
            "Google Public CA", "https://dv.acme-v02.api.pki.goog/directory",
            true, false, CertificateAuthority.GoogleStaging,
            "Google wymaga EAB: utworz klucz w GCP (Certificate Manager > Public CA > External Account Binding)."),
        [CertificateAuthority.GoogleStaging] = new(
            "Google Public CA (Staging)", "https://dv.acme-v02.test-api.pki.goog/directory",
            true, true, null,
            "Staging Google tez wymaga EAB (oddzielny klucz testowy z GCP)."),
        [CertificateAuthority.CustomAcme] = new(
            "Custom ACME server", null,
            false, false, null,
            null)
    };

    public static CaInfo Get(CertificateAuthority ca) => Infos[ca];
    public static string DisplayName(CertificateAuthority ca) => Infos.TryGetValue(ca, out var i) ? i.Display : ca.ToString();
    public static bool RequiresEab(CertificateAuthority ca) => Infos.TryGetValue(ca, out var i) && i.RequiresEab;
    public static bool IsStaging(CertificateAuthority ca) => Infos.TryGetValue(ca, out var i) && i.IsStaging;

    /// <summary>
    /// CA, dla ktorych EAB mozna pobrac automatycznie z samego emaila
    /// (ZeroSSL: api.zerossl.com/acme/eab-credentials-email). Wtedy EAB w formularzu jest opcjonalny.
    /// </summary>
    public static bool CanAutoFetchEab(CertificateAuthority ca) => ca == CertificateAuthority.ZeroSsl;

    /// <summary>Wolne CA (ZeroSSL potrafi trzymac order w "processing" kilka minut) - dluzsze odpytywanie.</summary>
    public static TimeSpan MaxPollTime(CertificateAuthority ca) =>
        ca == CertificateAuthority.ZeroSsl ? TimeSpan.FromMinutes(10) : TimeSpan.FromMinutes(3);

    public static bool TryGetStaging(CertificateAuthority ca, out CertificateAuthority staging)
    {
        staging = default;
        if (!Infos.TryGetValue(ca, out var i) || i.StagingCounterpart == null) return false;
        staging = i.StagingCounterpart.Value;
        return true;
    }

    public static string ResolveDirectoryUrl(CertificateAuthority ca, string? customUrl)
    {
        if (ca == CertificateAuthority.CustomAcme)
        {
            if (string.IsNullOrWhiteSpace(customUrl))
                throw new InvalidOperationException("Custom ACME wymaga Directory URL.");
            return customUrl.Trim();
        }
        return Infos[ca].DirectoryUrl!;
    }
}
