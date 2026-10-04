using System.Text.RegularExpressions;
using Certify.Core.Localization;
using Certify.Core.Models;
using Certify.Core.Services;

namespace Certify.Tests;

public class ManagedCertificateTests
{
    [Fact]
    public void DaysUntilExpiry_FloorsSoRecentlyExpiredIsNegative()
    {
        // Regresja: (int) obcinal -0.5 do 0 ("wygasa dzis" zamiast "wygasl").
        Assert.True(new ManagedCertificate { DateExpiry = DateTime.UtcNow.AddHours(-12) }.DaysUntilExpiry < 0);
        Assert.Equal(0, new ManagedCertificate { DateExpiry = DateTime.UtcNow.AddHours(12) }.DaysUntilExpiry);
        Assert.Equal(int.MaxValue, new ManagedCertificate().DaysUntilExpiry);
    }

    [Fact]
    public void RenewalDueAndExpired()
    {
        var c = new ManagedCertificate { RenewalDaysBeforeExpiry = 30, DateExpiry = DateTime.UtcNow.AddDays(40) };
        Assert.False(c.IsRenewalDue);
        c.DateExpiry = DateTime.UtcNow.AddDays(29);
        Assert.True(c.IsRenewalDue);
        Assert.False(c.IsExpired);
        c.DateExpiry = DateTime.UtcNow.AddMinutes(-1);
        Assert.True(c.IsExpired);
        Assert.False(new ManagedCertificate().IsRenewalDue);
    }

    [Fact]
    public void NextPlannedRenewal_PrefersExplicitAttempt()
    {
        var expiry = DateTime.UtcNow.AddDays(60);
        var c = new ManagedCertificate { DateExpiry = expiry, RenewalDaysBeforeExpiry = 30 };
        Assert.Equal(expiry.AddDays(-30), c.NextPlannedRenewal);
        var retry = DateTime.UtcNow.AddHours(12);
        c.DateNextRenewalAttempt = retry;
        Assert.Equal(retry, c.NextPlannedRenewal);
    }

    [Fact]
    public void ElapsedLifetimePercent()
    {
        var now = DateTime.UtcNow;
        Assert.Null(new ManagedCertificate { DateCreated = null }.ElapsedLifetimePercent);
        var half = new ManagedCertificate { DateRenewed = now.AddDays(-45), DateExpiry = now.AddDays(45) };
        Assert.InRange(half.ElapsedLifetimePercent!.Value, 49.9, 50.1);
        var expired = new ManagedCertificate { DateRenewed = now.AddDays(-100), DateExpiry = now.AddDays(-10) };
        Assert.Equal(100, expired.ElapsedLifetimePercent);
    }

    [Fact]
    public void CaDisplayName_UsesLastAttemptedCa()
    {
        var c = new ManagedCertificate { CertificateAuthority = CertificateAuthority.LetsEncrypt };
        Assert.Equal("Let's Encrypt", c.CaDisplayName);
        c.LastAttemptedCa = nameof(CertificateAuthority.LetsEncryptStaging);
        Assert.Equal("Let's Encrypt (Staging)", c.CaDisplayName);
        c.LastAttemptedCa = "garbage";
        Assert.Equal("Let's Encrypt", c.CaDisplayName);
    }

    [Fact]
    public void PrimaryDomain_IsFirstOrEmpty()
    {
        Assert.Equal("", new ManagedCertificate().PrimaryDomain);
        Assert.Equal("a.com", new ManagedCertificate { Domains = ["a.com", "b.com"] }.PrimaryDomain);
    }
}

public class HistoryAlertsCalendarTests
{
    [Fact]
    public void HistoryLog_TrimsToMaxAndTruncatesMessage()
    {
        var c = new ManagedCertificate();
        for (var i = 0; i < CertificateHistoryLog.MaxEntries + 15; i++)
            CertificateHistoryLog.Add(c, HistoryKind.Renew, i % 2 == 0, "m" + i);
        Assert.Equal(CertificateHistoryLog.MaxEntries, c.History.Count);
        Assert.Equal("m15", c.History[0].Message);
        CertificateHistoryLog.Add(c, HistoryKind.Deploy, false, new string('x', 500));
        Assert.Equal(200, c.History[^1].Message.Length);
        var (total, ok, failed) = CertificateHistoryLog.Stats(c);
        Assert.Equal(total, ok + failed);
    }

    [Fact]
    public void AlertsBuilder_Classifies()
    {
        var certs = new List<ManagedCertificate>
        {
            new() { Id = "exp", Name = "Expired", Domains = ["a.com"], DateExpiry = DateTime.UtcNow.AddHours(-2) },
            new() { Id = "soon", Name = "Soon", Domains = ["b.com"], DateExpiry = DateTime.UtcNow.AddDays(5) },
            new() { Id = "ok", Name = "Ok", Domains = ["c.com"], DateExpiry = DateTime.UtcNow.AddDays(80) },
            new() { Id = "err", Name = "Err", Domains = ["d.com"], Status = CertificateStatus.Error, StatusMessage = "boom" }
        };
        var a = AlertsBuilder.Build(certs, 14);
        Assert.Equal(["exp"], a.Expired.Select(x => x.CertId));
        Assert.Equal(["soon"], a.ExpiringSoon.Select(x => x.CertId));
        Assert.Equal(["err"], a.Errors.Select(x => x.CertId));
        Assert.Contains("ok", a.UpcomingRenewals.Select(x => x.CertId));
        Assert.DoesNotContain("err", a.UpcomingRenewals.Select(x => x.CertId));
        Assert.True(a.HasAny);
        Assert.False(AlertsBuilder.Build([certs[2]], 14).HasAny);
    }

    [Fact]
    public void RenewalCalendar_Is6WeeksStartingMonday()
    {
        var m = RenewalCalendar.Build([], 2026, 10);
        Assert.Equal(42, m.Days.Count);
        Assert.Equal(DayOfWeek.Monday, m.Days[0].Date.DayOfWeek);
        Assert.Equal(31, m.Days.Count(d => d.IsCurrentMonth));
        Assert.Equal(new DateTime(2026, 10, 1), m.Days.First(d => d.IsCurrentMonth).Date);
    }

    [Fact]
    public void RenewalCalendar_PlacesRenewalAndExpiry()
    {
        var expiryLocal = new DateTime(2026, 10, 20, 12, 0, 0, DateTimeKind.Local);
        var c = new ManagedCertificate { Id = "x", Name = "X", DateExpiry = expiryLocal.ToUniversalTime(), RenewalDaysBeforeExpiry = 10 };
        var m = RenewalCalendar.Build([c], 2026, 10);
        Assert.Contains(m.Days.Single(d => d.Date == new DateTime(2026, 10, 20)).Entries, e => e.Kind == CalendarEntryKind.Expiry);
        Assert.Contains(m.Days.Single(d => d.Date == new DateTime(2026, 10, 10)).Entries, e => e.Kind == CalendarEntryKind.RenewalDue);
        c.RenewalMode = RenewalMode.Manual;
        Assert.DoesNotContain(RenewalCalendar.Build([c], 2026, 10).Days.SelectMany(d => d.Entries), e => e.Kind == CalendarEntryKind.RenewalDue);
    }

    [Fact]
    public void CalendarTitle_PerLanguage()
    {
        var m = RenewalCalendar.Build([], 2026, 10);
        Assert.Equal("October 2026", m.GetTitle("en"));
        Assert.Contains("2026", m.GetTitle("pl"));
        Assert.Equal("Oktober 2026", m.GetTitle("de"));
        // fa domyslnie ma kalendarz perski - tytul ma byc gregorianski jak siatka.
        Assert.Contains("2026", m.GetTitle("fa").Replace("۲۰۲۶", "2026"));
    }
}

public class PreviewAndCatalogTests
{
    private static ManagedCertificate Valid() => new()
    {
        Name = "n", EmailAddress = "a@b.pl", Domains = ["example.com"],
        ChallengeConfig = new ChallengeConfig { HttpChallengeRoot = @"C:\www" },
        DeploymentTargets = [new DeploymentTarget { TargetType = DeploymentTargetType.IIS, SiteId = "Default" }]
    };

    [Fact]
    public void Preview_ValidCertHasNoErrors() => Assert.False(CertificatePreviewBuilder.Build(Valid()).HasErrors);

    [Fact]
    public void Preview_WildcardRequiresDns01()
    {
        var c = Valid();
        c.Domains = ["*.example.com"];
        Assert.True(CertificatePreviewBuilder.Build(c).HasErrors);
        c.ChallengeType = ChallengeType.Dns01;
        Assert.False(CertificatePreviewBuilder.Build(c).HasErrors);
    }

    [Fact]
    public void Preview_IpOnlyWithLetsEncrypt()
    {
        var c = Valid();
        c.Domains = ["203.0.113.5"];
        Assert.False(CertificatePreviewBuilder.Build(c).HasErrors);
        c.CertificateAuthority = CertificateAuthority.Google;
        c.EabKeyId = "k"; c.EabHmacKey = "h";
        Assert.True(CertificatePreviewBuilder.Build(c).HasErrors);
    }

    [Fact]
    public void Preview_EabRequiredExceptZeroSslAuto()
    {
        var c = Valid();
        c.CertificateAuthority = CertificateAuthority.Google;
        Assert.True(CertificatePreviewBuilder.Build(c).HasErrors);
        c.CertificateAuthority = CertificateAuthority.ZeroSsl;
        var pv = CertificatePreviewBuilder.Build(c);
        Assert.False(pv.HasErrors);
        Assert.Contains(pv.Warnings, w => w.Text.Contains("a@b.pl"));
    }

    [Fact]
    public void Preview_MissingWebrootEmailOrDnsCreds()
    {
        var c = Valid();
        c.ChallengeConfig.HttpChallengeRoot = null;
        Assert.True(CertificatePreviewBuilder.Build(c).HasErrors);
        c = Valid();
        c.EmailAddress = "nope";
        Assert.True(CertificatePreviewBuilder.Build(c).HasErrors);
        c = Valid();
        c.ChallengeType = ChallengeType.Dns01;
        c.ChallengeConfig.DnsProvider = "Cloudflare";
        Assert.True(CertificatePreviewBuilder.Build(c).HasErrors);
    }

    [Fact]
    public void Preview_CustomAcmeNeedsUrl()
    {
        var c = Valid();
        c.CertificateAuthority = CertificateAuthority.CustomAcme;
        Assert.True(CertificatePreviewBuilder.Build(c).HasErrors);
        c.CustomAcmeDirectoryUrl = "https://pebble:14000/dir";
        var pv = CertificatePreviewBuilder.Build(c);
        Assert.False(pv.HasErrors);
        Assert.Equal("https://pebble:14000/dir", pv.DirectoryUrl);
    }

    [Fact]
    public void Catalog_StagingCounterparts()
    {
        Assert.True(CertificateAuthorityCatalog.TryGetStaging(CertificateAuthority.LetsEncrypt, out var s));
        Assert.Equal(CertificateAuthority.LetsEncryptStaging, s);
        Assert.True(CertificateAuthorityCatalog.TryGetStaging(CertificateAuthority.Google, out s));
        Assert.Equal(CertificateAuthority.GoogleStaging, s);
        Assert.False(CertificateAuthorityCatalog.TryGetStaging(CertificateAuthority.ZeroSsl, out _));
        Assert.False(CertificateAuthorityCatalog.TryGetStaging(CertificateAuthority.CustomAcme, out _));
    }

    [Fact]
    public void Catalog_EveryCaHasInfoAndHttpsUrl()
    {
        foreach (var ca in Enum.GetValues<CertificateAuthority>().Where(c => c != CertificateAuthority.CustomAcme))
            Assert.StartsWith("https://", CertificateAuthorityCatalog.ResolveDirectoryUrl(ca, null));
        Assert.Equal(Enum.GetValues<CertificateAuthority>().Length, CertificateAuthorityCatalog.All.Count);
        Assert.Throws<InvalidOperationException>(() => CertificateAuthorityCatalog.ResolveDirectoryUrl(CertificateAuthority.CustomAcme, " "));
    }

    [Fact]
    public void Catalog_EabAndPolling()
    {
        Assert.True(CertificateAuthorityCatalog.RequiresEab(CertificateAuthority.ZeroSsl));
        Assert.True(CertificateAuthorityCatalog.CanAutoFetchEab(CertificateAuthority.ZeroSsl));
        Assert.False(CertificateAuthorityCatalog.CanAutoFetchEab(CertificateAuthority.Google));
        Assert.False(CertificateAuthorityCatalog.RequiresEab(CertificateAuthority.LetsEncrypt));
        Assert.True(CertificateAuthorityCatalog.MaxPollTime(CertificateAuthority.ZeroSsl) > CertificateAuthorityCatalog.MaxPollTime(CertificateAuthority.LetsEncrypt));
    }
}

public class AppSettingsTests
{
    [Fact]
    public void EmailRecipients_SplitsAndFilters()
    {
        var s = new AppSettings { EmailTo = "a@b.pl; c@d.pl ,not-an-email,,a@b.pl" };
        Assert.Equal(["a@b.pl", "c@d.pl"], s.EmailRecipients);
    }

    [Fact]
    public void Clone_IsIndependentAndCopyFromKeepsDataDirectory()
    {
        var s = new AppSettings { DataDirectory = @"C:\a", SmtpHost = "h1", SmtpPort = 25 };
        var c = s.Clone();
        c.SmtpHost = "h2";
        Assert.Equal("h1", s.SmtpHost);
        Assert.Equal(@"C:\a", c.DataDirectory);
        var other = new AppSettings { DataDirectory = @"C:\b", SmtpHost = "h3", SmtpPort = 587 };
        s.CopyFrom(other);
        Assert.Equal("h3", s.SmtpHost);
        Assert.Equal(587, s.SmtpPort);
        Assert.Equal(@"C:\a", s.DataDirectory);
    }

    [Fact]
    public void Paths_AreUnderDataDirectory()
    {
        var s = new AppSettings { DataDirectory = @"C:\data" };
        Assert.Equal(@"C:\data\managed_certificates.json", s.CertificatesJsonPath);
        Assert.Equal(@"C:\data\logs", s.LogsDirectory);
    }

    [Fact]
    public void GetDirectoryUrl_CustomRequiresUrl()
    {
        var s = new AppSettings();
        Assert.Equal(s.LetsEncryptStagingUrl, s.GetDirectoryUrl(CertificateAuthority.LetsEncryptStaging));
        Assert.Throws<InvalidOperationException>(() => s.GetDirectoryUrl(CertificateAuthority.CustomAcme));
        Assert.Equal("https://x/dir", s.GetDirectoryUrl(CertificateAuthority.CustomAcme, " https://x/dir "));
    }
}

/// <summary>UIStrings.Lang jest statyczny - te testy nie moga isc rownolegle z innymi.</summary>
[CollectionDefinition(nameof(LanguageCollection), DisableParallelization = true)]
public class LanguageCollection;

[Collection(nameof(LanguageCollection))]
public class UIStringsTests
{
    private static readonly Regex Placeholder = new(@"\{(\d+)\}");

    private static string Raw(string key, string lang)
    {
        var prev = UIStrings.Lang;
        UIStrings.Lang = lang;
        try { return UIStrings.T(key); }
        finally { UIStrings.Lang = prev; }
    }

    [Fact]
    public void EveryKey_HasPolishAndEnglishText()
    {
        var missing = UIStrings.Keys.Where(k => string.IsNullOrWhiteSpace(Raw(k, "pl")) || string.IsNullOrWhiteSpace(Raw(k, "en"))).ToList();
        Assert.Empty(missing);
    }

    [Fact]
    public void EveryKey_HasSamePlaceholdersInBothLanguages()
    {
        static HashSet<string> Ph(string s) => Placeholder.Matches(s).Select(m => m.Value).ToHashSet();
        var mismatched = UIStrings.Keys.Where(k => !Ph(Raw(k, "pl")).SetEquals(Ph(Raw(k, "en")))).ToList();
        Assert.Empty(mismatched);
    }

    [Fact]
    public void T_FormatsUnknownKeyAndBadArgs()
    {
        Assert.Equal("No_Such_Key", UIStrings.T("No_Such_Key"));
        Assert.True(UIStrings.Has("Yes"));
        // Za malo argumentow nie rzuca - zwraca szablon.
        Assert.Contains("{1}", UIStrings.T("Mail_Subject", 1));
    }

    public static TheoryData<string> TranslatedLanguages()
    {
        var data = new TheoryData<string>();
        foreach (var (code, _) in UIStrings.Languages.Where(l => l.Code is not ("pl" or "en"))) data.Add(code);
        return data;
    }

    [Theory]
    [MemberData(nameof(TranslatedLanguages))]
    public void Translation_HasEveryKey_WithEnglishPlaceholders(string lang)
    {
        static HashSet<string> Ph(string s) => Placeholder.Matches(s).Select(m => m.Value).ToHashSet();
        var tr = UIStrings.Translation(lang);
        Assert.Equal([], UIStrings.Keys.Where(k => !tr.TryGetValue(k, out var v) || string.IsNullOrWhiteSpace(v)).ToList());
        Assert.Equal([], tr.Keys.Where(k => !UIStrings.Has(k)).ToList());
        Assert.Equal([], UIStrings.Keys.Where(k => !Ph(tr[k]).SetEquals(Ph(Raw(k, "en")))).ToList());
        // Kalendarz dzieli Cal_Dow po ';' na 7 dni.
        Assert.Equal(7, tr["Cal_Dow"].Split(';').Length);
    }

    [Fact]
    public void T_UsesTranslation_AndFallsBackToEnglish()
    {
        Assert.Equal("Abbrechen", Raw("Btn_Cancel", "de"));
        Assert.Equal("Cancel", Raw("Btn_Cancel", "xx"));
    }

    [Fact]
    public void Languages_AreTwentyUniqueCodes()
    {
        Assert.Equal(20, UIStrings.Languages.Select(l => l.Code).Distinct().Count());
        Assert.True(UIStrings.IsRightToLeft("ar"));
        Assert.False(UIStrings.IsRightToLeft("pl"));
    }

    [Theory]
    [InlineData("pl", "en", "pl")]
    [InlineData("en", "pl", "en")]
    [InlineData("de", "pl", "de")]
    [InlineData("Auto", "pl", "pl")]
    [InlineData("Auto", "de", "de")]
    [InlineData("Auto", "zh", "zh")]
    [InlineData("Auto", "sw", "en")]
    [InlineData("xx", "fr", "fr")]
    public void ResolveLang(string setting, string os, string expected) =>
        Assert.Equal(expected, UIStrings.ResolveLang(setting, os));
}
