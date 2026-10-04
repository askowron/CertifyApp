using System.Security.Cryptography.X509Certificates;
using Certify.Core.Models;
using Certify.Core.Services;

namespace Certify.Tests;

public class CertificateArtifactWriterTests
{
    [Theory]
    [InlineData("example.com", "example.com")]
    [InlineData("*.example.com", "_wildcard.example.com")]
    [InlineData("2001:db8::1", "2001_db8__1")]
    [InlineData("  ", "certificate")]
    public void SafeFileName(string domain, string expected) =>
        Assert.Equal(expected, CertificateArtifactWriter.SafeFileName(domain));

    [Fact]
    public void SplitPemBlocks_FindsAllCertificates()
    {
        using var chain = new TestChain();
        var blocks = CertificateArtifactWriter.SplitPemBlocks(chain.FullChainPem + "\n-----BEGIN PRIVATE KEY-----\nx\n-----END PRIVATE KEY-----");
        Assert.Equal(3, blocks.Count);
        Assert.All(blocks, b => Assert.StartsWith("-----BEGIN CERTIFICATE-----", b));
    }

    [Fact]
    public void BuildPfx_HasLeafWithKeyAndChainWithoutDuplicateLeaf()
    {
        using var chain = new TestChain();
        var col = new X509Certificate2Collection();
        col.Import(chain.BuildPfx(), CertificateArtifactWriter.PfxPassword, X509KeyStorageFlags.EphemeralKeySet);
        Assert.Equal(3, col.Count);
        Assert.Single(col.Cast<X509Certificate2>(), c => c.HasPrivateKey && c.Thumbprint == chain.Leaf.Thumbprint);
        Assert.Contains(col.Cast<X509Certificate2>(), c => c.Thumbprint == chain.Intermediate.Thumbprint);
    }

    [Fact]
    public async Task WriteAsync_WritesArtifactsAndUpdatesModel()
    {
        using var tmp = new TempDir();
        using var chain = new TestChain("*.example.com");
        var cert = new ManagedCertificate { Domains = ["*.example.com"], DateNextRenewalAttempt = DateTime.UtcNow.AddHours(5), Status = CertificateStatus.Error };
        await CertificateArtifactWriter.WriteAsync(tmp.Path, cert, chain.LeafPem, chain.FullChainPem, chain.KeyPem, chain.BuildPfx(), isRenewal: true);

        var dir = Path.Combine(tmp.Path, "certs", cert.Id);
        Assert.Equal(Path.Combine(dir, "_wildcard.example.com.pfx"), cert.PfxPath);
        Assert.True(File.Exists(cert.PfxPath));
        Assert.True(File.Exists(cert.CertPath));
        Assert.True(File.Exists(cert.KeyPath));
        Assert.True(File.Exists(Path.Combine(dir, "chain.pem")));
        Assert.Equal(CertificateStatus.Valid, cert.Status);
        Assert.Null(cert.DateNextRenewalAttempt);
        Assert.Equal(CertificateArtifactWriter.PfxPassword, cert.PfxPassword);
        Assert.Equal(chain.Leaf.NotAfter.ToUniversalTime(), cert.DateExpiry!.Value, TimeSpan.FromSeconds(1));
    }
}

public class CertificateExporterTests : IAsyncLifetime
{
    private readonly TempDir _tmp = new();
    private readonly TestChain _chain = new();
    private readonly ManagedCertificate _cert = new() { Domains = ["example.com"] };

    public async Task InitializeAsync() =>
        await CertificateArtifactWriter.WriteAsync(_tmp.Path, _cert, _chain.LeafPem, _chain.FullChainPem, _chain.KeyPem, _chain.BuildPfx(), false);

    public Task DisposeAsync()
    {
        _chain.Dispose();
        _tmp.Dispose();
        return Task.CompletedTask;
    }

    private async Task<string> ExportAsync(CertificateExportFormat f)
    {
        var dest = Path.Combine(_tmp.Path, "out", f + f.DefaultExtension());
        var res = await new CertificateExporter().ExportAsync(_cert, f, dest);
        Assert.True(res.Success, res.Message);
        Assert.Equal(dest, res.ExportedPath);
        return dest;
    }

    private string[] Thumbprints(string path) =>
        CertificateArtifactWriter.SplitPemBlocks(File.ReadAllText(path))
            .Select(b => X509Certificate2.CreateFromPem(b).Thumbprint).ToArray();

    [Fact]
    public async Task PemPrimary_IsLeafOnly() =>
        Assert.Equal([_chain.Leaf.Thumbprint], Thumbprints(await ExportAsync(CertificateExportFormat.PemPrimary)));

    [Fact]
    public async Task IntermediateWithRoot() =>
        Assert.Equal([_chain.Intermediate.Thumbprint, _chain.Root.Thumbprint], Thumbprints(await ExportAsync(CertificateExportFormat.PemIntermediateWithRoot)));

    [Fact]
    public async Task IntermediateOnly_DropsSelfSignedRoot() =>
        Assert.Equal([_chain.Intermediate.Thumbprint], Thumbprints(await ExportAsync(CertificateExportFormat.PemIntermediateOnly)));

    [Fact]
    public async Task PrimaryWithIntermediate() =>
        Assert.Equal([_chain.Leaf.Thumbprint, _chain.Intermediate.Thumbprint], Thumbprints(await ExportAsync(CertificateExportFormat.PemPrimaryWithIntermediate)));

    [Fact]
    public async Task FullChain_WithAndWithoutKey()
    {
        var without = await ExportAsync(CertificateExportFormat.PemFullChainExcludingKey);
        Assert.Equal(3, Thumbprints(without).Length);
        Assert.DoesNotContain("PRIVATE KEY", File.ReadAllText(without));
        var with = await ExportAsync(CertificateExportFormat.PemFullChainIncludingKey);
        Assert.Equal(3, Thumbprints(with).Length);
        Assert.Contains("-----BEGIN PRIVATE KEY-----", File.ReadAllText(with));
    }

    [Fact]
    public async Task PrivateKeyAndPfx()
    {
        Assert.StartsWith("-----BEGIN PRIVATE KEY-----", File.ReadAllText(await ExportAsync(CertificateExportFormat.PemPrivateKey)));
        using var pfx = new X509Certificate2(await ExportAsync(CertificateExportFormat.Pfx), CertificateArtifactWriter.PfxPassword, X509KeyStorageFlags.EphemeralKeySet);
        Assert.True(pfx.HasPrivateKey);
    }

    [Fact]
    public async Task FailsWithoutIssuedCertOrDestination()
    {
        var exporter = new CertificateExporter();
        Assert.False((await exporter.ExportAsync(new ManagedCertificate(), CertificateExportFormat.PemPrimary, _tmp.File("x"))).Success);
        Assert.False((await exporter.ExportAsync(_cert, CertificateExportFormat.PemPrimary, " ")).Success);
    }

    [Fact]
    public void FormatMetadata_IsCompleteForEveryFormat()
    {
        foreach (var f in Enum.GetValues<CertificateExportFormat>())
        {
            Assert.NotEqual(f.ToString(), f.DisplayName());
            Assert.StartsWith(".", f.DefaultExtension());
            Assert.Contains("|", f.DialogFilter());
        }
    }
}

[Collection(nameof(LanguageCollection))]
public class EmailNotifierTests
{
    private static AppSettings Configured() => new()
    {
        EnableEmailNotifications = true, SmtpHost = "localhost", SmtpPort = 25,
        EmailFrom = "certify@example.com", EmailTo = "admin@example.com"
    };

    [Fact]
    public void ValidateConfig()
    {
        Assert.Null(new EmailNotifier(Configured()).ValidateConfig());
        var s = Configured(); s.SmtpHost = "";
        Assert.NotNull(new EmailNotifier(s).ValidateConfig());
        s = Configured(); s.SmtpPort = 70000;
        Assert.NotNull(new EmailNotifier(s).ValidateConfig());
        s = Configured(); s.EmailTo = "nobody";
        Assert.NotNull(new EmailNotifier(s).ValidateConfig());
    }

    [Fact]
    public void BuildDigest_ListsResultsAndExpiring()
    {
        var results = new List<RenewalReportItem>
        {
            new("Good", "a.com", true, "Renewed", DateTime.UtcNow.AddDays(90)),
            new("Bad", "b.com", false, "rate limited", null)
        };
        var expiring = new List<ManagedCertificate> { new() { Name = "Soon", Domains = ["c.com"], DateExpiry = DateTime.UtcNow.AddDays(3) } };
        var (subject, body) = EmailNotifier.BuildDigest(results, expiring, 14);
        Assert.Contains("1", subject);
        Assert.Contains("Good", body);
        Assert.Contains("rate limited", body);
        Assert.Contains("Soon", body);
    }

    [Fact]
    public async Task SendAsync_ToPickupDirectory_WritesEml()
    {
        using var tmp = new TempDir();
        var (ok, msg) = await new EmailNotifier(Configured()).SendAsync("Subj", "Body", tmp.Path);
        Assert.True(ok, msg);
        Assert.Single(Directory.GetFiles(tmp.Path, "*.eml"));
    }

    [Fact]
    public async Task SendAsync_InvalidConfig_DoesNotThrow()
    {
        var (ok, _) = await new EmailNotifier(new AppSettings()).SendAsync("s", "b");
        Assert.False(ok);
    }
}
