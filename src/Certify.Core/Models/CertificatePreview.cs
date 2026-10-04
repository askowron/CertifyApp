namespace Certify.Core.Models;

using Certify.Core.Localization;

public record PreviewWarning(string Text, bool IsError);

public record CertificatePreview(
    string Name,
    string PrimaryDomain,
    List<string> Domains,
    string CaDisplay,
    string DirectoryUrl,
    bool IsStaging,
    string ChallengeDisplay,
    string ChallengeDetail,
    List<string> DeploymentDisplays,
    int PreTaskCount,
    int DeploymentTaskCount,
    string RenewalDisplay,
    string ExpiryDisplay,
    List<PreviewWarning> Warnings)
{
    public bool HasErrors => Warnings.Any(w => w.IsError);
}

/// <summary>
/// Buduje podsumowanie requestu (jak CTW: Preview) + walidacje.
/// Czysta logika, bez UI - testowalna.
/// </summary>
public static class CertificatePreviewBuilder
{
    public static CertificatePreview Build(ManagedCertificate cert)
    {
        var warnings = new List<PreviewWarning>();
        void Warn(string t, bool err = false) => warnings.Add(new PreviewWarning(t, err));

        if (cert.Domains.Count == 0)
            Warn(UIStrings.T("Pv_NoDomains"), true);
        var hasIp = cert.Domains.Any(d => System.Net.IPAddress.TryParse(d.Trim().Trim('[', ']'), out _));
        if (hasIp)
        {
            if (cert.CertificateAuthority is not (CertificateAuthority.LetsEncrypt or CertificateAuthority.LetsEncryptStaging))
                Warn(UIStrings.T("Pv_IpOnlyLe"), true);
            else
                Warn(UIStrings.T("Pv_IpInfo"));
        }
        if (cert.Domains.Any(d => d.StartsWith("*.") ) && cert.ChallengeType != ChallengeType.Dns01)
            Warn(UIStrings.T("Pv_Wildcard"), true);
        if (string.IsNullOrWhiteSpace(cert.EmailAddress) || !cert.EmailAddress.Contains("@"))
            Warn(UIStrings.T("Pv_Email"), true);

        string challengeDetail;
        if (cert.ChallengeType == ChallengeType.Http01)
        {
            var roots = cert.ChallengeConfig.HttpChallengeRootPaths;
            challengeDetail = cert.ChallengeConfig.HttpChallengeRoot
                ?? (roots.Count > 0 ? string.Join(", ", roots.Select(kv => $"{kv.Key}->{kv.Value}")) : "");
            if (string.IsNullOrWhiteSpace(challengeDetail))
                Warn(UIStrings.T("Pv_Webroot"), true);
            else
                challengeDetail = UIStrings.T("Pv_WebrootDetail", challengeDetail);
        }
        else
        {
            var provider = cert.ChallengeConfig.DnsProvider;
            challengeDetail = provider.Equals("Cloudflare", StringComparison.OrdinalIgnoreCase)
                ? UIStrings.T("Pv_DnsCf")
                : provider.Equals("Route53", StringComparison.OrdinalIgnoreCase)
                ? UIStrings.T("Pv_DnsR53")
                : UIStrings.T("Pv_DnsManual");
            if (provider.Equals("Cloudflare", StringComparison.OrdinalIgnoreCase))
            {
                if (!cert.ChallengeConfig.DnsProviderCredentials.TryGetValue("CloudflareApiToken", out var tok)
                    || string.IsNullOrWhiteSpace(tok))
                    Warn(UIStrings.T("Pv_CfToken"), true);
            }
            if (provider.Equals("Route53", StringComparison.OrdinalIgnoreCase))
            {
                if (!cert.ChallengeConfig.DnsProviderCredentials.TryGetValue("AwsAccessKeyId", out var ak)
                    || string.IsNullOrWhiteSpace(ak)
                    || !cert.ChallengeConfig.DnsProviderCredentials.TryGetValue("AwsSecretAccessKey", out var sk)
                    || string.IsNullOrWhiteSpace(sk))
                    Warn(UIStrings.T("Pv_R53Creds"), true);
            }
        }

        string directoryUrl;
        try
        {
            directoryUrl = CertificateAuthorityCatalog.ResolveDirectoryUrl(
                cert.CertificateAuthority, cert.CustomAcmeDirectoryUrl);
        }
        catch (Exception ex)
        {
            directoryUrl = "-";
            Warn(ex.Message, true);
        }

        if (CertificateAuthorityCatalog.RequiresEab(cert.CertificateAuthority)
            && (string.IsNullOrWhiteSpace(cert.EabKeyId) || string.IsNullOrWhiteSpace(cert.EabHmacKey)))
        {
            var caName = CertificateAuthorityCatalog.DisplayName(cert.CertificateAuthority);
            if (CertificateAuthorityCatalog.CanAutoFetchEab(cert.CertificateAuthority))
                Warn(UIStrings.T("Pv_EabAuto", caName, cert.EmailAddress));
            else
                Warn(UIStrings.T("Pv_Eab", caName), true);
        }

        var deployments = cert.DeploymentTargets
            .Select(d => d.TargetType switch
            {
                DeploymentTargetType.IIS => UIStrings.T("Preview_Depl_Iis", d.SiteId ?? "-"),
                DeploymentTargetType.Apache => UIStrings.T("Preview_Depl_Apache", d.ConfigPath ?? "-"),
                DeploymentTargetType.Nginx => UIStrings.T("Preview_Depl_Nginx", d.ConfigPath ?? "-"),
                _ => d.TargetType.ToString()
            }).ToList();
        if (deployments.Count == 0)
            Warn(UIStrings.T("Pv_NoTargets"));

        if (cert.Status == CertificateStatus.Valid && !cert.IsExpired)
            Warn(UIStrings.T("Pv_Active", cert.DateExpiry?.ToLocalTime().ToString("yyyy-MM-dd")));

        return new CertificatePreview(
            string.IsNullOrWhiteSpace(cert.Name) ? cert.PrimaryDomain : cert.Name,
            cert.PrimaryDomain,
            cert.Domains.ToList(),
            cert.CaDisplayName,
            directoryUrl,
            CertificateAuthorityCatalog.IsStaging(cert.CertificateAuthority),
            cert.ChallengeType == ChallengeType.Http01 ? "http-01" : "dns-01",
            challengeDetail,
            deployments,
            cert.Tasks.Count(t => t.Enabled && t.Stage == CertificateTaskStage.PreRequest),
            cert.Tasks.Count(t => t.Enabled && t.Stage == CertificateTaskStage.Deployment),
            cert.RenewalMode == RenewalMode.Auto
                ? UIStrings.T("Preview_Renew_Auto", cert.RenewalDaysBeforeExpiry)
                : UIStrings.T("Preview_Renew_Manual"),
            cert.DateExpiry?.ToLocalTime().ToString("yyyy-MM-dd") ?? "-",
            warnings);
    }
}
