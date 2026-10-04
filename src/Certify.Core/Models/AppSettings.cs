namespace Certify.Core.Models;

public class AppSettings
{
    public string DataDirectory { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "CertifyApp");

    public string LetsEncryptDirectoryUrl { get; set; } = "https://acme-v02.api.letsencrypt.org/directory";
    public string LetsEncryptStagingUrl { get; set; } = "https://acme-staging-v02.api.letsencrypt.org/directory";

    public string GetDirectoryUrl(CertificateAuthority ca) => ca switch
    {
        CertificateAuthority.LetsEncryptStaging => LetsEncryptStagingUrl,
        // Nowe CA przez katalog (URL-e zweryfikowane z dokumentacja CA).
        CertificateAuthority.ZeroSsl => CertificateAuthorityCatalog.Get(ca).DirectoryUrl!,
        CertificateAuthority.Google => CertificateAuthorityCatalog.Get(ca).DirectoryUrl!,
        CertificateAuthority.GoogleStaging => CertificateAuthorityCatalog.Get(ca).DirectoryUrl!,
        CertificateAuthority.CustomAcme => throw new InvalidOperationException("Custom ACME wymaga URL z certyfikatu (CustomAcmeDirectoryUrl)."),
        _ => LetsEncryptDirectoryUrl
    };

    public string GetDirectoryUrl(CertificateAuthority ca, string? customUrl) =>
        CertificateAuthorityCatalog.ResolveDirectoryUrl(ca, customUrl);

    public string? DefaultEmail { get; set; }
    public bool EnableBackgroundRenewal { get; set; } = true;
    public TimeSpan RenewalCheckInterval { get; set; } = TimeSpan.FromHours(12);

    // persistence file
    public string CertificatesJsonPath => Path.Combine(DataDirectory, "managed_certificates.json");
    public string AccountsJsonPath => Path.Combine(DataDirectory, "acme_accounts.json");
    public string LogsDirectory => Path.Combine(DataDirectory, "logs");
    public string SettingsJsonPath => Path.Combine(DataDirectory, "appsettings.json");

    // Powiadomienia e-mail (SMTP). Haslo w appsettings.json plain text (jak inne sekrety lokalne).
    public bool EnableEmailNotifications { get; set; } = false;
    public string SmtpHost { get; set; } = string.Empty;
    public int SmtpPort { get; set; } = 587;
    public bool SmtpUseSsl { get; set; } = true;
    public string SmtpUsername { get; set; } = string.Empty;
    public string SmtpPassword { get; set; } = string.Empty;
    public string EmailFrom { get; set; } = string.Empty;
    // Odbiorcy rozdzieleni ; lub ,
    public string EmailTo { get; set; } = string.Empty;
    // Dla certow wygasajacych wczesniej niz X dni wysylaj ostrzezenie w digescie.
    public int ExpiryWarningDays { get; set; } = 14;

    public List<string> EmailRecipients => EmailTo
        .Split([';', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Where(a => a.Contains('@')).Distinct().ToList();

    // Jezyk UI: Auto (z systemu) / pl / en.
    public string Language { get; set; } = "Auto";

    /// <summary>
    /// Przepisuje wszystkie zapisywalne ustawienia z innej instancji (bez DataDirectory).
    /// Serwisy trzymaja referencje do jednej instancji - podmieniamy wartosci, nie obiekt.
    /// </summary>
    public void CopyFrom(AppSettings other)
    {
        foreach (var p in typeof(AppSettings).GetProperties())
        {
            if (!p.CanRead || !p.CanWrite || p.Name == nameof(DataDirectory)) continue;
            p.SetValue(this, p.GetValue(other));
        }
    }

    public AppSettings Clone()
    {
        var c = new AppSettings { DataDirectory = DataDirectory };
        c.CopyFrom(this);
        return c;
    }
}
