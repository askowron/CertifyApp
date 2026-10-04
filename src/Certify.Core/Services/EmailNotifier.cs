using System.Net;
using System.Net.Mail;
using System.Text;
using Certify.Core.Localization;
using Certify.Core.Models;

namespace Certify.Core.Services;

public record RenewalReportItem(string CertName, string Domains, bool Success, string Message, DateTime? DateExpiry);

/// <summary>
/// Powiadomienia e-mail (SMTP): digest odnawiania + ostrzezenia o wygasaniu.
/// pickupDirectory (testy) = zapis .eml zamiast wysylki.
/// </summary>
public class EmailNotifier(AppSettings settings)
{
    private readonly AppSettings _settings = settings;

    public bool Enabled => _settings.EnableEmailNotifications;

    public string? ValidateConfig()
    {
        if (string.IsNullOrWhiteSpace(_settings.SmtpHost)) return UIStrings.T("Cfg_Err_Host");
        if (_settings.SmtpPort is < 1 or > 65535) return UIStrings.T("Cfg_Err_Port");
        if (string.IsNullOrWhiteSpace(_settings.EmailFrom) || !_settings.EmailFrom.Contains('@')) return UIStrings.T("Cfg_Err_From");
        if (_settings.EmailRecipients.Count == 0) return UIStrings.T("Cfg_Err_To");
        return null;
    }

    public static (string Subject, string Body) BuildDigest(
        List<RenewalReportItem> results, List<ManagedCertificate> expiringSoon, int warningDays)
    {
        var ok = results.Count(r => r.Success);
        var fail = results.Count - ok;
        var subject = UIStrings.T("Mail_Subject", ok, fail,
            expiringSoon.Count > 0 ? UIStrings.T("Mail_Subject_Exp", expiringSoon.Count) : "");
        var sb = new StringBuilder();
        sb.AppendLine(UIStrings.T("Mail_Body_Header", DateTime.Now.ToString("yyyy-MM-dd HH:mm")));
        sb.AppendLine(UIStrings.T("Mail_Body_Summary", results.Count, ok, fail));
        sb.AppendLine();
        foreach (var r in results)
        {
            sb.AppendLine($"{(r.Success ? "OK  " : UIStrings.T("Mail_Row_Fail"))} {r.CertName} ({r.Domains})");
            if (!string.IsNullOrWhiteSpace(r.Message)) sb.AppendLine($"       {r.Message}");
            if (r.DateExpiry.HasValue) sb.AppendLine(UIStrings.T("Mail_Body_ExpDate", r.DateExpiry.Value.ToLocalTime().ToString("yyyy-MM-dd")));
        }
        if (expiringSoon.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine(UIStrings.T("Mail_Body_ExpHeader", warningDays));
            foreach (var c in expiringSoon.OrderBy(c => c.DateExpiry))
                sb.AppendLine(UIStrings.T("Mail_Body_ExpRow", c.Name, c.PrimaryDomain,
                    c.DateExpiry?.ToLocalTime().ToString("yyyy-MM-dd"), c.DaysUntilExpiry));
        }
        return (subject, sb.ToString());
    }

    public static (string Subject, string Body) BuildFailure(string certName, string domains, string message) =>
        (UIStrings.T("Mail_Fail_Subject", certName),
         UIStrings.T("Mail_Fail_Body", certName, domains, DateTime.Now.ToString("yyyy-MM-dd HH:mm"), message));

    public async Task<(bool Success, string Message)> SendAsync(string subject, string body, string? pickupDirectory = null)
    {
        var err = ValidateConfig();
        if (err != null) return (false, err);
        try
        {
            using var msg = new MailMessage
            {
                From = new MailAddress(_settings.EmailFrom),
                Subject = subject,
                Body = body
            };
            foreach (var to in _settings.EmailRecipients) msg.To.Add(to);
            using var client = pickupDirectory != null
                ? new SmtpClient { DeliveryMethod = SmtpDeliveryMethod.SpecifiedPickupDirectory, PickupDirectoryLocation = pickupDirectory }
                : new SmtpClient(_settings.SmtpHost, _settings.SmtpPort)
                {
                    EnableSsl = _settings.SmtpUseSsl,
                    Credentials = string.IsNullOrWhiteSpace(_settings.SmtpUsername)
                        ? CredentialCache.DefaultNetworkCredentials
                        : new NetworkCredential(_settings.SmtpUsername, _settings.SmtpPassword)
                };
            if (pickupDirectory != null) Directory.CreateDirectory(pickupDirectory);
            await client.SendMailAsync(msg);
            return (true, "OK");
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    public Task<(bool Success, string Message)> SendDigestAsync(
        List<RenewalReportItem> results, List<ManagedCertificate> expiringSoon, int warningDays)
    {
        var (subject, body) = BuildDigest(results, expiringSoon, warningDays);
        return SendAsync(subject, body);
    }
}
