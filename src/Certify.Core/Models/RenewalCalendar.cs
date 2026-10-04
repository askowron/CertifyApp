namespace Certify.Core.Models;

public enum CalendarEntryKind
{
    RenewalDue,
    Expiry
}

public record CalendarEntry(string CertId, string CertName, CalendarEntryKind Kind);

public record CalendarDay(DateTime Date, bool IsCurrentMonth, List<CalendarEntry> Entries);

public record CalendarMonth(int Year, int Month, List<CalendarDay> Days)
{
    public string Title => GetTitle("pl");

    public string GetTitle(string lang) => new DateTime(Year, Month, 1).ToString("MMMM yyyy", GregorianCulture(lang));

    // Siatka jest gregorianska - kultury z innym domyslnym kalendarzem (fa: perski)
    // dostaja gregorianski, zeby tytul zgadzal sie z dniami.
    private static System.Globalization.CultureInfo GregorianCulture(string lang)
    {
        System.Globalization.CultureInfo ci;
        try { ci = (System.Globalization.CultureInfo)System.Globalization.CultureInfo.GetCultureInfo(lang == "en" ? "en-US" : lang).Clone(); }
        catch (System.Globalization.CultureNotFoundException) { return System.Globalization.CultureInfo.GetCultureInfo("en-US"); }
        if (ci.DateTimeFormat.Calendar is not System.Globalization.GregorianCalendar)
        {
            var greg = ci.OptionalCalendars.OfType<System.Globalization.GregorianCalendar>().FirstOrDefault();
            if (greg == null) return System.Globalization.CultureInfo.GetCultureInfo("en-US");
            ci.DateTimeFormat.Calendar = greg;
        }
        return ci;
    }
}

/// <summary>
/// Kalendarz odnowien: siatka 6x7 (poniedzialek-niedziela) z wpisami
/// RenewalDue (planowane odnowienie, tryb Auto) i Expiry (wygasniecie).
/// Czysta logika - testowalna.
/// </summary>
public static class RenewalCalendar
{
    public static CalendarMonth Build(IEnumerable<ManagedCertificate> certs, int year, int month)
    {
        var first = new DateTime(year, month, 1);
        // Poniedzialek = 0.
        var lead = ((int)first.DayOfWeek + 6) % 7;
        var start = first.AddDays(-lead);
        var list = certs.ToList();

        var days = new List<CalendarDay>();
        for (var i = 0; i < 42; i++)
        {
            var date = start.AddDays(i);
            var entries = new List<CalendarEntry>();
            foreach (var c in list)
            {
                var name = string.IsNullOrWhiteSpace(c.Name) ? c.PrimaryDomain : c.Name;
                if (c.RenewalMode == RenewalMode.Auto && c.NextPlannedRenewal.HasValue
                    && c.NextPlannedRenewal.Value.ToLocalTime().Date == date)
                    entries.Add(new CalendarEntry(c.Id, name, CalendarEntryKind.RenewalDue));
                if (c.DateExpiry.HasValue && c.DateExpiry.Value.ToLocalTime().Date == date)
                    entries.Add(new CalendarEntry(c.Id, name, CalendarEntryKind.Expiry));
            }
            entries.Sort((a, b) => a.Kind.CompareTo(b.Kind));
            days.Add(new CalendarDay(date, date.Month == month, entries));
        }
        return new CalendarMonth(year, month, days);
    }
}
