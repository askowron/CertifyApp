using System.Diagnostics;
using Certify.Core.Models;
using Certify.Core.Services;

namespace Certify.Tests;

internal sealed class FakeCa(Func<ManagedCertificate, CancellationToken, CertificateRequestResult> renew) : ICertificateAuthorityProvider
{
    public List<string> Renewed { get; } = new();

    public Task<CertificateRequestResult> RequestCertificateAsync(ManagedCertificate c, IProgress<string>? log = null, CancellationToken ct = default) =>
        RenewCertificateAsync(c, log, ct);

    public Task<CertificateRequestResult> RenewCertificateAsync(ManagedCertificate c, IProgress<string>? log = null, CancellationToken ct = default)
    {
        Renewed.Add(c.Id);
        return Task.FromResult(renew(c, ct));
    }

    public static CertificateRequestResult Issue(ManagedCertificate c)
    {
        c.DateExpiry = DateTime.UtcNow.AddDays(90);
        c.DateNextRenewalAttempt = null;
        c.Status = CertificateStatus.Valid;
        return new CertificateRequestResult { Success = true, Message = "OK", Certificate = c };
    }
}

internal sealed class FakeDeployer(DeploymentTargetType type, bool success) : IDeploymentTarget
{
    public int Calls { get; private set; }
    public DeploymentTargetType TargetType => type;

    public Task<DeploymentResult> DeployAsync(ManagedCertificate cert, DeploymentTarget config, IProgress<string>? log, CancellationToken ct)
    {
        Calls++;
        return Task.FromResult(new DeploymentResult { Success = success, Message = success ? "ok" : "binding failed" });
    }

    public Task<List<string>> DiscoverSitesAsync() => Task.FromResult(new List<string>());
}

[Collection(nameof(LanguageCollection))]
public class RenewalServiceTests
{
    private static ManagedCertificate Due(string name) => new()
    {
        Name = name, Domains = [name + ".com"], DateExpiry = DateTime.UtcNow.AddDays(5),
        DeploymentTargets = [new DeploymentTarget { TargetType = DeploymentTargetType.IIS, SiteId = "s" }]
    };

    [Fact]
    public async Task RenewsOnlyDueAutoCertificates()
    {
        using var tmp = new TempDir();
        var store = new CertificateStore(tmp.Settings());
        var due = Due("due");
        var notDue = Due("later"); notDue.DateExpiry = DateTime.UtcNow.AddDays(80);
        var manual = Due("manual"); manual.RenewalMode = RenewalMode.Manual;
        var errored = Due("err"); errored.DateExpiry = DateTime.UtcNow.AddDays(80); errored.Status = CertificateStatus.Error;
        foreach (var c in new[] { due, notDue, manual, errored }) await store.UpsertAsync(c);

        var ca = new FakeCa((c, _) => FakeCa.Issue(c));
        await new RenewalService(store, ca, [new FakeDeployer(DeploymentTargetType.IIS, true)], tmp.Settings()).CheckAndRenewAllAsync();

        Assert.Equal(new[] { due.Id, errored.Id }.OrderBy(x => x), ca.Renewed.OrderBy(x => x));
    }

    [Fact]
    public async Task Success_ClearsRetryDeploysAndRecordsHistory()
    {
        using var tmp = new TempDir();
        var store = new CertificateStore(tmp.Settings());
        var cert = Due("a");
        cert.DateNextRenewalAttempt = DateTime.UtcNow.AddHours(3);
        await store.UpsertAsync(cert);
        var deployer = new FakeDeployer(DeploymentTargetType.IIS, true);

        await new RenewalService(store, new FakeCa((c, _) => FakeCa.Issue(c)), [deployer], tmp.Settings()).CheckAndRenewAllAsync();

        var saved = Assert.Single(await store.LoadAllAsync());
        Assert.Equal(CertificateStatus.Valid, saved.Status);
        Assert.Null(saved.DateNextRenewalAttempt);
        Assert.Equal(saved.DateExpiry!.Value.AddDays(-saved.RenewalDaysBeforeExpiry), saved.NextPlannedRenewal);
        Assert.Equal(1, deployer.Calls);
        Assert.Contains(saved.History, h => h.Kind == HistoryKind.Renew && h.Success);
    }

    [Fact]
    public async Task CaFailure_SetsErrorAndRetryIn12h()
    {
        using var tmp = new TempDir();
        var store = new CertificateStore(tmp.Settings());
        await store.UpsertAsync(Due("a"));
        var deployer = new FakeDeployer(DeploymentTargetType.IIS, true);

        await new RenewalService(store, new FakeCa((c, _) => new CertificateRequestResult { Success = false, Message = "rejected" }),
            [deployer], tmp.Settings()).CheckAndRenewAllAsync();

        var saved = Assert.Single(await store.LoadAllAsync());
        Assert.Equal(CertificateStatus.Error, saved.Status);
        Assert.Equal("rejected", saved.StatusMessage);
        Assert.InRange(saved.DateNextRenewalAttempt!.Value, DateTime.UtcNow.AddHours(11), DateTime.UtcNow.AddHours(13));
        Assert.Equal(0, deployer.Calls);
        Assert.Contains(saved.History, h => h.Kind == HistoryKind.Renew && !h.Success);
    }

    [Fact]
    public async Task DeployFailure_IsReportedNotSwallowed()
    {
        // Regresja: wynik deployerow byl ignorowany - historia/status mowily "sukces".
        using var tmp = new TempDir();
        var store = new CertificateStore(tmp.Settings());
        await store.UpsertAsync(Due("a"));

        await new RenewalService(store, new FakeCa((c, _) => FakeCa.Issue(c)),
            [new FakeDeployer(DeploymentTargetType.IIS, false)], tmp.Settings()).CheckAndRenewAllAsync();

        var saved = Assert.Single(await store.LoadAllAsync());
        Assert.Equal(CertificateStatus.Valid, saved.Status);
        Assert.Contains("binding failed", saved.StatusMessage);
        Assert.Contains(saved.History, h => h.Kind == HistoryKind.Deploy && !h.Success);
    }

    [Fact]
    public async Task Cancellation_PropagatesAndDoesNotMarkError()
    {
        using var tmp = new TempDir();
        var store = new CertificateStore(tmp.Settings());
        await store.UpsertAsync(Due("a"));
        await store.UpsertAsync(Due("b"));
        using var cts = new CancellationTokenSource();
        var ca = new FakeCa((c, ct) => { cts.Cancel(); ct.ThrowIfCancellationRequested(); return FakeCa.Issue(c); });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new RenewalService(store, ca, [], tmp.Settings()).CheckAndRenewAllAsync(null, cts.Token));

        Assert.Single(ca.Renewed);
        Assert.All(await store.LoadAllAsync(), c => Assert.NotEqual(CertificateStatus.Error, c.Status));
    }

    [Fact]
    public async Task FailingPreRequestTask_SkipsRenewal()
    {
        using var tmp = new TempDir();
        var store = new CertificateStore(tmp.Settings());
        var cert = Due("a");
        cert.Tasks.Add(new CertificateTaskDefinition
        {
            Name = "check", Stage = CertificateTaskStage.PreRequest, TaskType = CertificateTaskType.RunProgram,
            Parameters = { [CertificateTaskParameters.P_ExecutablePath] = Path.Combine(tmp.Path, "missing.exe") }
        });
        await store.UpsertAsync(cert);
        var ca = new FakeCa((c, _) => FakeCa.Issue(c));

        await new RenewalService(store, ca, [], tmp.Settings()).CheckAndRenewAllAsync();

        Assert.Empty(ca.Renewed);
        var saved = Assert.Single(await store.LoadAllAsync());
        Assert.Equal(CertificateTaskLastStatus.Failed, saved.Tasks[0].LastStatus);
        Assert.NotNull(saved.DateNextRenewalAttempt);
    }

    [Fact]
    public async Task DeployAsync_ReportsMissingDeployer()
    {
        using var tmp = new TempDir();
        var svc = new RenewalService(new CertificateStore(tmp.Settings()), new FakeCa((c, _) => FakeCa.Issue(c)),
            [new FakeDeployer(DeploymentTargetType.IIS, true)], tmp.Settings());
        var cert = Due("a");
        Assert.Null(await svc.DeployAsync(cert));
        cert.DeploymentTargets.Add(new DeploymentTarget { TargetType = DeploymentTargetType.Nginx });
        Assert.Contains("Nginx", await svc.DeployAsync(cert));
    }

    [Fact]
    public async Task BackgroundService_SkipsTickWhileOperationRuns()
    {
        using var tmp = new TempDir();
        var store = new CertificateStore(tmp.Settings());
        await store.UpsertAsync(Due("a"));
        var ca = new FakeCa((c, _) => FakeCa.Issue(c));
        var svc = new RenewalService(store, ca, [], tmp.Settings());
        var bg = new BackgroundRenewalService(svc, tmp.Settings());
        var tick = typeof(BackgroundRenewalService).GetMethod("TickAsync", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;

        await svc.OperationLock.WaitAsync();
        await (Task)tick.Invoke(bg, null)!;
        Assert.Empty(ca.Renewed);
        svc.OperationLock.Release();

        await (Task)tick.Invoke(bg, null)!;
        Assert.Single(ca.Renewed);
        Assert.Equal(1, svc.OperationLock.CurrentCount);
    }
}

[Collection(nameof(LanguageCollection))]
public class CertificateTaskRunnerTests
{
    private static CertificateTaskDefinition Task(CertificateTaskType type, CertificateTaskStage stage, params (string Key, string Value)[] p)
    {
        var t = new CertificateTaskDefinition { Name = type.ToString(), TaskType = type, Stage = stage, Trigger = CertificateTaskTrigger.Always };
        foreach (var (k, v) in p) t.Parameters[k] = v;
        return t;
    }

    [Fact]
    public async Task RunProgram_ExpandsVariables()
    {
        using var tmp = new TempDir();
        var outFile = tmp.File("out.txt");
        var cert = new ManagedCertificate { Name = "MyCert", Domains = ["a.com", "b.com"] };
        cert.Tasks.Add(Task(CertificateTaskType.RunProgram, CertificateTaskStage.PreRequest,
            (CertificateTaskParameters.P_ExecutablePath, "cmd.exe"),
            (CertificateTaskParameters.P_Arguments, $"/c echo {{Name}} {{Domains}}> \"{outFile}\"")));

        Assert.True(await new CertificateTaskRunner().RunPreRequestTasksAsync(cert, null, CancellationToken.None));
        Assert.Contains("MyCert a.com,b.com", File.ReadAllText(outFile));
        Assert.Equal(CertificateTaskLastStatus.Success, cert.Tasks[0].LastStatus);
    }

    [Fact]
    public async Task NonZeroExitCode_FailsPreRequest()
    {
        var cert = new ManagedCertificate();
        cert.Tasks.Add(Task(CertificateTaskType.RunProgram, CertificateTaskStage.PreRequest,
            (CertificateTaskParameters.P_ExecutablePath, "cmd.exe"), (CertificateTaskParameters.P_Arguments, "/c exit 3")));
        Assert.False(await new CertificateTaskRunner().RunPreRequestTasksAsync(cert, null, CancellationToken.None));
        Assert.Equal(CertificateTaskLastStatus.Failed, cert.Tasks[0].LastStatus);
        Assert.Contains("3", cert.Tasks[0].LastMessage);
    }

    [Fact]
    public async Task DisabledTasks_AreIgnored()
    {
        var cert = new ManagedCertificate();
        var t = Task(CertificateTaskType.RunProgram, CertificateTaskStage.PreRequest,
            (CertificateTaskParameters.P_ExecutablePath, "cmd.exe"), (CertificateTaskParameters.P_Arguments, "/c exit 1"));
        t.Enabled = false;
        cert.Tasks.Add(t);
        Assert.True(await new CertificateTaskRunner().RunPreRequestTasksAsync(cert, null, CancellationToken.None));
        Assert.Equal(CertificateTaskLastStatus.NeverRun, t.LastStatus);
    }

    [Fact]
    public async Task DeploymentTasks_RespectTriggerAndWebhookFilter()
    {
        var cert = new ManagedCertificate();
        var onSuccess = Task(CertificateTaskType.Wait, CertificateTaskStage.Deployment, (CertificateTaskParameters.P_Seconds, "0"));
        onSuccess.Trigger = CertificateTaskTrigger.OnSuccess;
        // Webhook tylko dla sukcesu przy porazce = Skipped bez wywolania sieci.
        var webhook = Task(CertificateTaskType.Webhook, CertificateTaskStage.Deployment,
            (CertificateTaskParameters.P_Url, "http://127.0.0.1:1/never"), (CertificateTaskParameters.P_OnStatus, "Success"));
        var always = Task(CertificateTaskType.Wait, CertificateTaskStage.Deployment, (CertificateTaskParameters.P_Seconds, "0"));
        cert.Tasks.AddRange([onSuccess, webhook, always]);

        await new CertificateTaskRunner().RunDeploymentTasksAsync(cert, certSuccess: false, null, CancellationToken.None);

        Assert.Equal(CertificateTaskLastStatus.Skipped, onSuccess.LastStatus);
        Assert.Equal(CertificateTaskLastStatus.Skipped, webhook.LastStatus);
        Assert.Equal(CertificateTaskLastStatus.Success, always.LastStatus);
    }

    [Fact]
    public async Task WaitTask_RejectsInvalidSeconds()
    {
        var cert = new ManagedCertificate();
        cert.Tasks.Add(Task(CertificateTaskType.Wait, CertificateTaskStage.PreRequest, (CertificateTaskParameters.P_Seconds, "-5")));
        Assert.False(await new CertificateTaskRunner().RunPreRequestTasksAsync(cert, null, CancellationToken.None));
    }

    [Fact]
    public async Task ExportTask_WritesFile()
    {
        using var tmp = new TempDir();
        using var chain = new TestChain();
        var cert = new ManagedCertificate { Domains = ["example.com"] };
        await CertificateArtifactWriter.WriteAsync(tmp.Path, cert, chain.LeafPem, chain.FullChainPem, chain.KeyPem, chain.BuildPfx(), false);
        var dest = tmp.File("export.crt");
        cert.Tasks.Add(Task(CertificateTaskType.ExportCertificate, CertificateTaskStage.Deployment,
            (CertificateTaskParameters.P_DestinationPath, dest), (CertificateTaskParameters.P_Format, nameof(CertificateExportFormat.PemPrimary))));

        await new CertificateTaskRunner().RunDeploymentTasksAsync(cert, true, null, CancellationToken.None);

        Assert.Equal(CertificateTaskLastStatus.Success, cert.Tasks[0].LastStatus);
        Assert.Contains("BEGIN CERTIFICATE", File.ReadAllText(dest));
    }

    [Fact]
    public async Task Cancellation_KillsProcessAndPropagates()
    {
        var cert = new ManagedCertificate();
        cert.Tasks.Add(Task(CertificateTaskType.RunProgram, CertificateTaskStage.PreRequest,
            (CertificateTaskParameters.P_ExecutablePath, "cmd.exe"), (CertificateTaskParameters.P_Arguments, "/c ping -n 30 127.0.0.1 >nul")));
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
        var sw = Stopwatch.StartNew();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new CertificateTaskRunner().RunPreRequestTasksAsync(cert, null, cts.Token));
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(10), $"took {sw.Elapsed}");
        Assert.Equal(CertificateTaskLastStatus.NeverRun, cert.Tasks[0].LastStatus);
    }

    [Fact]
    public void Descriptors_ExistForEveryTaskType()
    {
        foreach (var t in Enum.GetValues<CertificateTaskType>())
            Assert.NotEmpty(CertificateTaskParameters.GetDescriptors(t));
    }
}
