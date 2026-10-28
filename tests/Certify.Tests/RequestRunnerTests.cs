using Certify.Core.Models;
using Certify.Core.Services;

namespace Certify.Tests;

[Collection(nameof(LanguageCollection))]
public class RequestRunnerTests
{
    private static ManagedCertificate Tpl(string domain, CertificateAuthority ca, DateTime renewed, bool expired = false) => new()
    {
        Name = domain, Domains = [domain], CertificateAuthority = ca, Status = CertificateStatus.Valid,
        ChallengeType = ChallengeType.Http01, DateRenewed = renewed,
        DateExpiry = expired ? DateTime.UtcNow.AddDays(-1) : DateTime.UtcNow.AddDays(60),
        EmailAddress = "ops@appit.pl", EabKeyId = "kid", EabHmacKey = "hmac",
        CustomAcmeDirectoryUrl = "https://acme.example/dir", RenewalDaysBeforeExpiry = 21,
        Tasks = [new CertificateTaskDefinition { Name = "t" }],
        DeploymentTargets = [new DeploymentTarget { TargetType = DeploymentTargetType.IIS, SiteId = "old" }]
    };

    private static (CertificateRequestRunner Runner, CertificateStore Store, FakeCa Ca, FakeDeployer Dep, RenewalService Renewal)
        Build(TempDir tmp, Func<ManagedCertificate, CancellationToken, CertificateRequestResult>? ca = null, bool deployOk = true)
    {
        var store = new CertificateStore(tmp.Settings());
        var fakeCa = new FakeCa(ca ?? ((c, _) => FakeCa.Issue(c)));
        var dep = new FakeDeployer(DeploymentTargetType.IIS, deployOk);
        var renewal = new RenewalService(store, fakeCa, [dep], tmp.Settings());
        return (new CertificateRequestRunner(store, fakeCa, renewal), store, fakeCa, dep, renewal);
    }

    private static Task<RequestOutcome> Run(CertificateRequestRunner r, string host = "x.appit.pl", string? template = null,
        IProgress<string>? log = null, int lockMs = 5000) =>
        r.RunAsync(host, "site1", @"C:\inetpub\x", template, TimeSpan.FromMilliseconds(lockMs), log, CancellationToken.None);

    [Fact]
    public void FindTemplate_PicksNewestValidZeroSslInZone()
    {
        var a = Tpl("a.appit.pl", CertificateAuthority.ZeroSsl, DateTime.UtcNow.AddDays(-30));
        var b = Tpl("b.appit.pl", CertificateAuthority.ZeroSsl, DateTime.UtcNow.AddDays(-10));
        var c = Tpl("c.appit.pl", CertificateAuthority.LetsEncrypt, DateTime.UtcNow.AddDays(-1));
        Assert.Equal("b.appit.pl", CertificateRequestRunner.FindTemplate([a, b, c], "x.appit.pl", null)!.PrimaryDomain);
    }

    [Fact]
    public void FindTemplate_SkipsExpiredAndOtherZone()
    {
        var a = Tpl("a.appit.pl", CertificateAuthority.ZeroSsl, DateTime.UtcNow.AddDays(-30), expired: true);
        var f = Tpl("finds.pl", CertificateAuthority.ZeroSsl, DateTime.UtcNow.AddDays(-1));
        Assert.Null(CertificateRequestRunner.FindTemplate([a, f], "x.appit.pl", null));
    }

    [Fact]
    public void FindTemplate_ApiHost_UsesAppitZone()
    {
        var g = Tpl("garage.appit.pl", CertificateAuthority.ZeroSsl, DateTime.UtcNow.AddDays(-1));
        Assert.Equal("garage.appit.pl", CertificateRequestRunner.FindTemplate([g], "x.api.appit.pl", null)!.PrimaryDomain);
    }

    [Fact]
    public void FindTemplate_ExplicitDomain()
    {
        var f = Tpl("finds.pl", CertificateAuthority.ZeroSsl, DateTime.UtcNow.AddDays(-1));
        var a = Tpl("a.appit.pl", CertificateAuthority.ZeroSsl, DateTime.UtcNow.AddDays(-1));
        Assert.Equal("finds.pl", CertificateRequestRunner.FindTemplate([a, f], "x.appit.pl", "finds.pl")!.PrimaryDomain);
    }

    [Fact]
    public void BuildFromTemplate_CopiesAccountFields_NotTasksOrTargets()
    {
        var t = Tpl("a.appit.pl", CertificateAuthority.ZeroSsl, DateTime.UtcNow);
        var c = CertificateRequestRunner.BuildFromTemplate(t, "x.appit.pl", "site1", @"C:\w");
        Assert.Equal(t.CertificateAuthority, c.CertificateAuthority);
        Assert.Equal("ops@appit.pl", c.EmailAddress);
        Assert.Equal("kid", c.EabKeyId);
        Assert.Equal("hmac", c.EabHmacKey);
        Assert.Equal("https://acme.example/dir", c.CustomAcmeDirectoryUrl);
        Assert.Equal(ChallengeType.Http01, c.ChallengeType);
        Assert.Equal(21, c.RenewalDaysBeforeExpiry);
        Assert.Empty(c.Tasks);
        var target = Assert.Single(c.DeploymentTargets);
        Assert.Equal(DeploymentTargetType.IIS, target.TargetType);
        Assert.Equal("site1", target.SiteId);
        Assert.Equal(@"C:\w", c.ChallengeConfig.HttpChallengeRootPaths["x.appit.pl"]);
        Assert.Equal(@"C:\w", c.ChallengeConfig.HttpChallengeRoot);
        Assert.Equal(RenewalMode.Auto, c.RenewalMode);
        Assert.NotEqual(t.Id, c.Id);
        Assert.Equal("x.appit.pl", c.Name);
        Assert.Equal(["x.appit.pl"], c.Domains);
    }

    [Fact]
    public async Task Request_New_IssuesDeploysAndStores()
    {
        using var tmp = new TempDir();
        var (runner, store, ca, dep, _) = Build(tmp);
        await store.UpsertAsync(Tpl("a.appit.pl", CertificateAuthority.ZeroSsl, DateTime.UtcNow));

        var o = await Run(runner);

        Assert.Equal(RequestResult.Issued, o.Result);
        Assert.Equal(0, o.ExitCode);
        var saved = Assert.Single((await store.LoadAllAsync()), c => c.Domains.Contains("x.appit.pl"));
        Assert.Contains(saved.History, h => h.Kind == HistoryKind.Request && h.Success);
        Assert.Equal(1, dep.Calls);
        Assert.Single(ca.Renewed);
    }

    [Fact]
    public async Task Request_ExistingValid_DeploysWithoutOrder()
    {
        using var tmp = new TempDir();
        var (runner, store, ca, dep, _) = Build(tmp);
        using var chain = new TestChain("x.appit.pl");
        var pfx = tmp.File("x.pfx");
        await File.WriteAllBytesAsync(pfx, chain.BuildPfx());
        var existing = Tpl("x.appit.pl", CertificateAuthority.ZeroSsl, DateTime.UtcNow);
        existing.PfxPath = pfx;
        existing.PfxPassword = "certify";
        await store.UpsertAsync(existing);

        var o = await Run(runner);

        Assert.Equal(RequestResult.Valid, o.Result);
        Assert.Equal(0, o.ExitCode);
        Assert.Empty(ca.Renewed);
        Assert.Equal(1, dep.Calls);
        Assert.Equal(chain.Leaf.Thumbprint, o.Thumbprint);
    }

    [Fact]
    public async Task Request_ExistingInvalid_ReordersSameId()
    {
        using var tmp = new TempDir();
        var (runner, store, ca, _, _) = Build(tmp);
        var existing = Tpl("x.appit.pl", CertificateAuthority.ZeroSsl, DateTime.UtcNow);
        existing.Status = CertificateStatus.Error;
        await store.UpsertAsync(existing);

        var o = await Run(runner);

        Assert.Equal(RequestResult.Issued, o.Result);
        var saved = Assert.Single(await store.LoadAllAsync());
        Assert.Equal(existing.Id, saved.Id);
        Assert.Equal([existing.Id], ca.Renewed);
    }

    [Fact]
    public async Task Request_NoTemplate_Failed()
    {
        using var tmp = new TempDir();
        var (runner, _, ca, _, _) = Build(tmp);

        var o = await Run(runner);

        Assert.Equal(RequestResult.Failed, o.Result);
        Assert.Equal(1, o.ExitCode);
        Assert.Contains("szablon", o.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(ca.Renewed);
    }

    [Fact]
    public async Task Request_CaFails_FailedAndHistory()
    {
        using var tmp = new TempDir();
        var (runner, store, _, dep, _) = Build(tmp, (_, _) => new CertificateRequestResult { Success = false, Message = "rejected" });
        await store.UpsertAsync(Tpl("a.appit.pl", CertificateAuthority.ZeroSsl, DateTime.UtcNow));

        var o = await Run(runner);

        Assert.Equal(RequestResult.Failed, o.Result);
        Assert.Contains("rejected", o.Message);
        var saved = Assert.Single(await store.LoadAllAsync(), c => c.Domains.Contains("x.appit.pl"));
        Assert.Equal(CertificateStatus.Error, saved.Status);
        Assert.Contains(saved.History, h => h.Kind == HistoryKind.Request && !h.Success);
        Assert.Equal(0, dep.Calls);
    }

    [Fact]
    public async Task Request_DeployFails_DeployFailedButStored()
    {
        using var tmp = new TempDir();
        var (runner, store, _, _, _) = Build(tmp, deployOk: false);
        await store.UpsertAsync(Tpl("a.appit.pl", CertificateAuthority.ZeroSsl, DateTime.UtcNow));

        var o = await Run(runner);

        Assert.Equal(RequestResult.DeployFailed, o.Result);
        Assert.Equal(5, o.ExitCode);
        Assert.Contains(await store.LoadAllAsync(), c => c.Domains.Contains("x.appit.pl"));
    }

    [Fact]
    public async Task Request_LockBusy_ReturnsBusy()
    {
        using var tmp = new TempDir();
        var (runner, store, ca, _, _) = Build(tmp);
        await store.UpsertAsync(Tpl("a.appit.pl", CertificateAuthority.ZeroSsl, DateTime.UtcNow));
        var other = new CrossProcessLock(Path.Combine(tmp.Path, CrossProcessLock.FileName));
        Assert.True(other.Wait(1000));
        try
        {
            var o = await Run(runner, lockMs: 200);
            Assert.Equal(RequestResult.Busy, o.Result);
            Assert.Equal(3, o.ExitCode);
            Assert.Empty(ca.Renewed);
        }
        finally { other.Release(); }
    }

    [Fact]
    public async Task Request_OutputHasNoEabSecrets()
    {
        using var tmp = new TempDir();
        var (runner, store, _, _, _) = Build(tmp);
        var t = Tpl("a.appit.pl", CertificateAuthority.ZeroSsl, DateTime.UtcNow);
        t.EabHmacKey = "HMAC-SECRET-123";
        t.EabKeyId = "KID-SECRET-456";
        await store.UpsertAsync(t);
        var log = new ListLog();

        var o = await Run(runner, log: log);

        Assert.Equal(RequestResult.Issued, o.Result);
        Assert.False(log.Contains("HMAC-SECRET-123"));
        Assert.False(log.Contains("KID-SECRET-456"));
        Assert.DoesNotContain("HMAC-SECRET-123", o.Message);
        Assert.DoesNotContain("KID-SECRET-456", o.Message);
    }

    private sealed class ThrowingDeployer : IDeploymentTarget
    {
        public DeploymentTargetType TargetType => DeploymentTargetType.IIS;
        public Task<DeploymentResult> DeployAsync(ManagedCertificate cert, DeploymentTarget config, IProgress<string>? log, CancellationToken ct) =>
            throw new InvalidOperationException("boom");
        public Task<List<string>> DiscoverSitesAsync() => Task.FromResult(new List<string>());
    }

    private static async Task<ManagedCertificate> ValidWithPfx(TempDir tmp, CertificateStore store, string name = "x.appit.pl")
    {
        var chain = new TestChain("x.appit.pl");
        var pfx = tmp.File("x.pfx");
        await File.WriteAllBytesAsync(pfx, chain.BuildPfx());
        var c = Tpl("x.appit.pl", CertificateAuthority.ZeroSsl, DateTime.UtcNow);
        c.Name = name; c.PfxPath = pfx; c.PfxPassword = "certify";
        await store.UpsertAsync(c);
        return c;
    }

    [Fact]
    public async Task Request_ReusedRecordWithOtherName_ReportsRequestedHost()
    {
        using var tmp = new TempDir();
        var (runner, store, _, _, _) = Build(tmp);
        await ValidWithPfx(tmp, store, name: "My site");

        var o = await Run(runner);

        Assert.Equal(RequestResult.Valid, o.Result);
        Assert.Equal("x.appit.pl", o.Host);
    }

    [Fact]
    public async Task Request_Issued_ReportsHostAndExpiry()
    {
        using var tmp = new TempDir();
        var (runner, store, _, _, _) = Build(tmp);
        await store.UpsertAsync(Tpl("a.appit.pl", CertificateAuthority.ZeroSsl, DateTime.UtcNow));

        var o = await Run(runner);

        Assert.Equal("x.appit.pl", o.Host);
        Assert.InRange(o.Expiry!.Value, DateTime.UtcNow.AddDays(89), DateTime.UtcNow.AddDays(91));
    }

    [Fact]
    public async Task Request_DeployFails_ReportsHostAndExpiry()
    {
        using var tmp = new TempDir();
        var (runner, store, _, _, _) = Build(tmp, deployOk: false);
        await store.UpsertAsync(Tpl("a.appit.pl", CertificateAuthority.ZeroSsl, DateTime.UtcNow));

        var o = await Run(runner);

        Assert.Equal(RequestResult.DeployFailed, o.Result);
        Assert.Equal("x.appit.pl", o.Host);
        Assert.NotNull(o.Expiry);
    }

    [Fact]
    public async Task Request_ValidStatusButMissingPfx_Reorders()
    {
        using var tmp = new TempDir();
        var (runner, store, ca, _, _) = Build(tmp);
        var existing = Tpl("x.appit.pl", CertificateAuthority.ZeroSsl, DateTime.UtcNow);
        existing.PfxPath = tmp.File("missing.pfx");
        await store.UpsertAsync(existing);

        var o = await Run(runner);

        Assert.Equal(RequestResult.Issued, o.Result);
        Assert.Equal([existing.Id], ca.Renewed);
    }

    [Fact]
    public async Task Request_ExistingValid_DeployFails_DeployFailedExit5()
    {
        using var tmp = new TempDir();
        var (runner, store, ca, _, _) = Build(tmp, deployOk: false);
        await ValidWithPfx(tmp, store);

        var o = await Run(runner);

        Assert.Equal(RequestResult.DeployFailed, o.Result);
        Assert.Equal(5, o.ExitCode);
        Assert.Empty(ca.Renewed);
    }

    [Fact]
    public async Task Request_ExistingValid_DeployThrows_Failed()
    {
        using var tmp = new TempDir();
        var store = new CertificateStore(tmp.Settings());
        var ca = new FakeCa((c, _) => FakeCa.Issue(c));
        var renewal = new RenewalService(store, ca, [new ThrowingDeployer()], tmp.Settings());
        await ValidWithPfx(tmp, store);

        var o = await Run(new CertificateRequestRunner(store, ca, renewal));

        Assert.Equal(RequestResult.Failed, o.Result);
        Assert.Contains("boom", o.Message);
        Assert.True(renewal.OperationLock.CurrentCount == 1);
    }

    [Fact]
    public async Task Request_PreRequestTaskFails_FailedWithoutOrder()
    {
        using var tmp = new TempDir();
        var (runner, store, ca, _, _) = Build(tmp);
        var existing = Tpl("x.appit.pl", CertificateAuthority.ZeroSsl, DateTime.UtcNow);
        existing.Status = CertificateStatus.Error;
        existing.Tasks = [new CertificateTaskDefinition
        {
            Name = "check", Stage = CertificateTaskStage.PreRequest, TaskType = CertificateTaskType.RunProgram,
            Parameters = { [CertificateTaskParameters.P_ExecutablePath] = tmp.File("missing.exe") }
        }];
        await store.UpsertAsync(existing);

        var o = await Run(runner);

        Assert.Equal(RequestResult.Failed, o.Result);
        Assert.Empty(ca.Renewed);
        var saved = Assert.Single(await store.LoadAllAsync());
        Assert.Equal(CertificateStatus.Error, saved.Status);
        Assert.Equal(Certify.Core.Localization.UIStrings.T("Renew_Msg_PreFail"), saved.StatusMessage);
    }

    [Fact]
    public async Task Request_Cancelled_ReleasesLock()
    {
        using var tmp = new TempDir();
        using var cts = new CancellationTokenSource();
        var (runner, store, _, _, renewal) = Build(tmp, (c, ct) => { cts.Cancel(); ct.ThrowIfCancellationRequested(); return FakeCa.Issue(c); });
        await store.UpsertAsync(Tpl("a.appit.pl", CertificateAuthority.ZeroSsl, DateTime.UtcNow));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            runner.RunAsync("x.appit.pl", "site1", @"C:\w", null, TimeSpan.FromSeconds(5), null, cts.Token));

        Assert.True(await renewal.OperationLock.WaitAsync(0, CancellationToken.None));
        renewal.OperationLock.Release();
    }
}
