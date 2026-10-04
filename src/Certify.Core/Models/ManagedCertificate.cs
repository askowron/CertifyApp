namespace Certify.Core.Models;

public class ManagedCertificate
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = string.Empty;
    public List<string> Domains { get; set; } = new();
    public string PrimaryDomain => Domains.FirstOrDefault() ?? string.Empty;

    public CertificateAuthority CertificateAuthority { get; set; } = CertificateAuthority.LetsEncrypt;
    public ChallengeType ChallengeType { get; set; } = ChallengeType.Http01;
    public string EmailAddress { get; set; } = string.Empty;

    // EAB (External Account Binding) - wymagane przez ZeroSSL / Google.
    // HMAC trzymany jako base64url dokladnie jak zwraca panel CA.
    public string? EabKeyId { get; set; }
    public string? EabHmacKey { get; set; }
    // Dla CustomAcme: pelny Directory URL serwera ACME.
    public string? CustomAcmeDirectoryUrl { get; set; }

    public List<DeploymentTarget> DeploymentTargets { get; set; } = new();

    // Zadania jak w CTW: Pre-Request (przed requestem) + Deployment (po wdrozeniu).
    // Kolejnosc na liscie = kolejnosc wykonania.
    public List<CertificateTaskDefinition> Tasks { get; set; } = new();

    // Historia operacji (Request/Test/Renew/Deploy/Export), max 100 wpisow.
    public List<CertificateHistoryEntry> History { get; set; } = new();

    // Paths
    public string? PfxPath { get; set; }
    public string? CertPath { get; set; }
    public string? KeyPath { get; set; }
    public string? PfxPassword { get; set; }

    // Status
    public CertificateStatus Status { get; set; } = CertificateStatus.NotStarted;
    public string StatusMessage { get; set; } = string.Empty;
    public DateTime? DateCreated { get; set; } = DateTime.UtcNow;
    public DateTime? DateExpiry { get; set; }
    public DateTime? DateRenewed { get; set; }
    public DateTime? DateNextRenewalAttempt { get; set; }

    public RenewalMode RenewalMode { get; set; } = RenewalMode.Auto;
    public int RenewalDaysBeforeExpiry { get; set; } = 30;

    // Nazwa CA uzytej przy ostatniej probie wystawienia/odnowienia
    // (np. po tescie Staging ro different od CertificateAuthority).
    public string? LastAttemptedCa { get; set; }

    public string CaDisplayName => CertificateAuthorityCatalog.DisplayName(
        Enum.TryParse<CertificateAuthority>(LastAttemptedCa, out var last) ? last : CertificateAuthority);

    // % wykorzystanego czasu zycia aktualnego certyfikatu (jak w Certify The Web:
    // "Elapsed Lifetime"). Start = ostatnie odnowienie (data wystawienia),
    // koniec = wygasniecie. Null gdy brak dat.
    public double? ElapsedLifetimePercent
    {
        get
        {
            var start = DateRenewed ?? DateCreated;
            if (start == null || DateExpiry == null) return null;
            var total = (DateExpiry.Value - start.Value).TotalSeconds;
            if (total <= 0) return 100;
            var elapsed = (DateTime.UtcNow - start.Value).TotalSeconds;
            return Math.Clamp(elapsed / total * 100.0, 0, 100);
        }
    }

    // Planowana data nastepnego odnowienia: jawnie wyliczona przy ostatnim
    // odnowieniu albo wygasniecie minus margines (jak w CTW "Next Planned Renewal").
    public DateTime? NextPlannedRenewal =>
        DateNextRenewalAttempt ?? DateExpiry?.AddDays(-RenewalDaysBeforeExpiry);

    // Challenge config
    public ChallengeConfig ChallengeConfig { get; set; } = new();

    public bool IncludeWWW { get; set; } = false;

    // Floor, nie obciecie: cert wygasly 12h temu to -1 (nie 0 = "wygasa dzis").
    public int DaysUntilExpiry => DateExpiry.HasValue ? (int)Math.Floor((DateExpiry.Value - DateTime.UtcNow).TotalDays) : int.MaxValue;
    public bool IsRenewalDue => DateExpiry.HasValue && (DateExpiry.Value - DateTime.UtcNow).TotalDays <= RenewalDaysBeforeExpiry;
    public bool IsExpired => DateExpiry.HasValue && DateExpiry.Value < DateTime.UtcNow;
}

public class ChallengeConfig
{
    // For Http01: path to webroot or site id. We support mapping domain->webroot
    public Dictionary<string, string> HttpChallengeRootPaths { get; set; } = new();
    // Fallback single root for all domains (e.g. C:\inetpub\wwwroot)
    public string? HttpChallengeRoot { get; set; }

    // For Dns01: provider config (Manual / Cloudflare). Token w DnsProviderCredentials["CloudflareApiToken"].
    public string DnsProvider { get; set; } = "Manual";
    public Dictionary<string, string> DnsProviderCredentials { get; set; } = new();
    // Maks. czas oczekiwania na propagacje TXT (Cloudflare, sek).
    public int DnsPropagationSeconds { get; set; } = 90;
}

public class DeploymentTarget
{
    public DeploymentTargetType TargetType { get; set; }
    // IIS
    public string? SiteId { get; set; } // IIS Site name
    // Apache / Nginx
    public string? ConfigPath { get; set; } // ścieżka do vhost config
    public string? CertificateOutputPath { get; set; } // gdzie skopiować cert
    public string? ServiceName { get; set; } // nazwa usługi do reload
}
