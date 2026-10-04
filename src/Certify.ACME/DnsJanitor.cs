using Certify.Core.Models;

namespace Certify.ACME;

/// <summary>
/// Sprzatanie osieroconych rekordow TXT _acme-challenge.* (po manualach,
/// przerwanych runach, crashach). Tylko dokladne nazwy dla domen certyfikatu.
/// </summary>
public static class DnsJanitor
{
    public record JanitorResult(string Domain, string Provider, int Deleted, string? Error);

    /// <summary>Domeny do sprzatania: distinct, bez wildcard prefix, bez kropek.</summary>
    public static List<string> TargetDomains(ManagedCertificate cert) =>
        cert.Domains.Select(d => d.Trim().TrimEnd('.').ToLowerInvariant())
            .Select(d => d.StartsWith("*.") ? d[2..] : d)
            .Where(d => d.Length > 0 && !IdentifierClassifier.IsIp(d))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    public static async Task<List<JanitorResult>> CleanupForCertAsync(
        ManagedCertificate cert, IProgress<string>? log, CancellationToken ct)
    {
        var results = new List<JanitorResult>();
        var provider = cert.ChallengeConfig.DnsProvider;
        var isCf = ChallengeHandlerFactory.IsCloudflare(cert);
        var isR53 = ChallengeHandlerFactory.IsRoute53(cert);
        if (!isCf && !isR53)
        {
            log?.Report($"[Janitor] Provider '{provider}': brak API (Manual?) - pomijam.");
            return results;
        }
        var label = isCf ? "Cloudflare" : "Route53";
        foreach (var domain in TargetDomains(cert))
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                int deleted = isCf
                    ? await new CloudflareDns01Handler(ChallengeHandlerFactory.CloudflareToken(cert)!,
                        cert.ChallengeConfig.DnsPropagationSeconds).CleanupStaleAsync(domain, log, ct)
                    : await new Route53Dns01Handler(ChallengeHandlerFactory.AwsKey(cert)!,
                        ChallengeHandlerFactory.AwsSecret(cert)!,
                        cert.ChallengeConfig.DnsPropagationSeconds).CleanupStaleAsync(domain, log, ct);
                results.Add(new JanitorResult(domain, label, deleted, null));
            }
            catch (Exception ex)
            {
                log?.Report($"[Janitor] {domain}: BŁĄD: {ex.Message}");
                results.Add(new JanitorResult(domain, label, 0, ex.Message));
            }
        }
        return results;
    }
}
