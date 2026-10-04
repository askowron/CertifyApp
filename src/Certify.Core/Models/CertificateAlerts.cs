namespace Certify.Core.Models;

using Certify.Core.Localization;

public record AlertItem(string CertId, string Title, string Detail);

public record CertificateAlerts(
    List<AlertItem> ExpiringSoon,
    List<AlertItem> Expired,
    List<AlertItem> Errors,
    List<AlertItem> UpcomingRenewals)
{
    public bool HasAny => ExpiringSoon.Count + Expired.Count + Errors.Count > 0;
    public string Summary(int warningDays)
    {
        var parts = new List<string>();
        if (Expired.Count > 0) parts.Add(UIStrings.T("Alerts_Sum_Expired", Expired.Count));
        if (ExpiringSoon.Count > 0) parts.Add(UIStrings.T("Alerts_Sum_Expiring", warningDays, ExpiringSoon.Count));
        if (Errors.Count > 0) parts.Add(UIStrings.T("Alerts_Sum_Errors", Errors.Count));
        return string.Join("  •  ", parts);
    }
}

/// <summary>
/// Dashboard alertow (wygasajace, wygasle, bledy, nadchodzace odnowienia).
/// Czysta logika - testowalna.
/// </summary>
public static class AlertsBuilder
{
    public static CertificateAlerts Build(IEnumerable<ManagedCertificate> certs, int warningDays)
    {
        var expiring = new List<(DateTime Expiry, AlertItem Item)>();
        var expired = new List<(DateTime Expiry, AlertItem Item)>();
        var errors = new List<AlertItem>();
        var renewals = new List<(DateTime When, AlertItem Item)>();

        foreach (var c in certs)
        {
            var title = string.IsNullOrWhiteSpace(c.Name) ? c.PrimaryDomain : c.Name;
            if (c.DateExpiry.HasValue)
            {
                var days = c.DaysUntilExpiry;
                var detail = days < 0
                    ? UIStrings.T("Alerts_ExpiredAgo", c.PrimaryDomain, -days, c.DateExpiry.Value.ToLocalTime().ToString("yyyy-MM-dd"))
                    : UIStrings.T("Alerts_ExpiringIn", c.PrimaryDomain, days, c.DateExpiry.Value.ToLocalTime().ToString("yyyy-MM-dd"));
                var item = new AlertItem(c.Id, title, detail);
                if (days < 0) expired.Add((c.DateExpiry.Value, item));
                else if (days <= warningDays) expiring.Add((c.DateExpiry.Value, item));
            }
            if (c.Status == CertificateStatus.Error)
                errors.Add(new AlertItem(c.Id, title, Truncate(c.StatusMessage, 120)));
            if (c.RenewalMode == RenewalMode.Auto && c.Status != CertificateStatus.Error
                && c.NextPlannedRenewal.HasValue && c.NextPlannedRenewal.Value >= DateTime.UtcNow)
                renewals.Add((c.NextPlannedRenewal.Value,
                    new AlertItem(c.Id, title, UIStrings.T("Alerts_RenewalAt", c.NextPlannedRenewal.Value.ToLocalTime().ToString("yyyy-MM-dd")))));
        }

        return new CertificateAlerts(
            expiring.OrderBy(x => x.Expiry).Select(x => x.Item).ToList(),
            expired.OrderBy(x => x.Expiry).Select(x => x.Item).ToList(),
            errors,
            renewals.OrderBy(x => x.When).Select(x => x.Item).ToList());
    }

    private static string Truncate(string s, int max) =>
        string.IsNullOrEmpty(s) ? "-" : (s.Length <= max ? s : s[..max] + "...");
}
