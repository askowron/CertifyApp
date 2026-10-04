using Certify.Core.Localization;
using Certify.Core.Models;

namespace Certify.Core.Services;

public class CertificateRequestResult
{
    public bool Success { get; set; }
    public string Message { get; set; } = string.Empty;
    public ManagedCertificate? Certificate { get; set; }
}

public interface ICertificateAuthorityProvider
{
    Task<CertificateRequestResult> RequestCertificateAsync(ManagedCertificate managedCert, IProgress<string>? log = null, CancellationToken ct = default);
    Task<CertificateRequestResult> RenewCertificateAsync(ManagedCertificate managedCert, IProgress<string>? log = null, CancellationToken ct = default);
}

public class RenewalService
{
    private readonly CertificateStore _store;
    private readonly ICertificateAuthorityProvider _caProvider;
    private readonly IEnumerable<IDeploymentTarget> _deploymentTargets;
    private readonly AppSettings _settings;
    private readonly CertificateTaskRunner _tasks = new();
    private readonly EmailNotifier? _email;

    /// <summary>
    /// Jedna operacja na certyfikatach naraz (UI, odnawianie w tle, usluga Windows,
    /// --renew). Bez tego tlo moglo odnawiac ten sam cert rownolegle z recznym Request.
    /// Dziala miedzy procesami (plik operation.lock w DataDirectory).
    /// </summary>
    public CrossProcessLock OperationLock { get; }

    public RenewalService(CertificateStore store, ICertificateAuthorityProvider caProvider, IEnumerable<IDeploymentTarget> deploymentTargets, AppSettings settings, EmailNotifier? email = null)
    {
        OperationLock = new CrossProcessLock(Path.Combine(settings.DataDirectory, CrossProcessLock.FileName));
        _store = store;
        _caProvider = caProvider;
        _deploymentTargets = deploymentTargets;
        _settings = settings;
        _email = email;
    }

    public async Task CheckAndRenewAllAsync(IProgress<string>? log = null, CancellationToken ct = default)
    {
        var all = await _store.LoadAllAsync();
        var report = new List<RenewalReportItem>();
        foreach (var cert in all.Where(c => c.RenewalMode == RenewalMode.Auto))
        {
            if (cert.IsRenewalDue || cert.IsExpired || cert.Status == CertificateStatus.Error)
            {
                log?.Report($"[Renewal] Sprawdzam {cert.Name} ({cert.PrimaryDomain}) - do wygaśnięcia {cert.DaysUntilExpiry} dni.");
                try
                {
                    cert.LastAttemptedCa = cert.CertificateAuthority.ToString();
                    if (!await _tasks.RunPreRequestTasksAsync(cert, log, ct))
                    {
                        cert.StatusMessage = UIStrings.T("Renew_Msg_PreFail");
                        log?.Report($"[Renewal] Pomijam {cert.Name}: pre-request task nie powiódł się.");
                        cert.DateNextRenewalAttempt = DateTime.UtcNow.AddHours(12);
                        CertificateHistoryLog.Add(cert, HistoryKind.Renew, false, "Pre-Request task failed");
                        await _store.UpsertAsync(cert);
                        report.Add(new RenewalReportItem(cert.Name, cert.PrimaryDomain, false, "Pre-Request task failed", cert.DateExpiry));
                        continue;
                    }
                    var result = await _caProvider.RenewCertificateAsync(cert, log, ct);
                    var reportOk = result.Success;
                    var reportMsg = result.Success ? UIStrings.T("Status_Renewed") : result.Message;
                    if (result.Success)
                    {
                        cert.Status = CertificateStatus.Valid;
                        cert.DateRenewed = DateTime.UtcNow;
                        cert.StatusMessage = UIStrings.T("Renew_Msg_Renewed") + DateTime.Now;
                        // Nastepne odnowienie wg daty wygasniecia (NextPlannedRenewal), nie za 12h.
                        cert.DateNextRenewalAttempt = null;
                        log?.Report($"[Renewal] Odnowiono {cert.Name} - sukces.");
                        CertificateHistoryLog.Add(cert, HistoryKind.Renew, true, UIStrings.T("Status_Renewed"));
                        var deployError = await DeployAsync(cert, log, ct);
                        if (deployError != null)
                        {
                            cert.StatusMessage = UIStrings.T("Deploy_Msg_Failed", deployError);
                            CertificateHistoryLog.Add(cert, HistoryKind.Deploy, false, deployError);
                            reportOk = false;
                            reportMsg = cert.StatusMessage;
                        }
                        await _tasks.RunDeploymentTasksAsync(cert, certSuccess: true, log, ct);
                    }
                    else
                    {
                        cert.Status = CertificateStatus.Error;
                        cert.StatusMessage = result.Message;
                        cert.DateNextRenewalAttempt = DateTime.UtcNow.AddHours(12);
                        log?.Report($"[Renewal] Błąd odnawiania {cert.Name}: {result.Message}");
                        await _tasks.RunDeploymentTasksAsync(cert, certSuccess: false, log, ct);
                        CertificateHistoryLog.Add(cert, HistoryKind.Renew, false, result.Message);
                    }
                    report.Add(new RenewalReportItem(cert.Name, string.Join(",", cert.Domains), reportOk, reportMsg, cert.DateExpiry));
                    await _store.UpsertAsync(cert);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    // Anulowanie przez uzytkownika: nie oznaczaj certu jako Error
                    // i nie przechodz do kolejnych (wczesniej kazdy dostawal Error).
                    log?.Report($"[Renewal] Anulowano podczas {cert.Name}.");
                    throw;
                }
                catch (Exception ex)
                {
                    cert.Status = CertificateStatus.Error;
                    cert.StatusMessage = ex.Message;
                    CertificateHistoryLog.Add(cert, HistoryKind.Renew, false, ex.Message);
                    await _store.UpsertAsync(cert);
                    log?.Report($"[Renewal] Wyjątek {cert.Name}: {ex.Message}");
                    report.Add(new RenewalReportItem(cert.Name, cert.PrimaryDomain, false, ex.Message, cert.DateExpiry));
                }
            }
        }
        await MaybeSendDigestAsync(all, report, log);
    }

    private async Task MaybeSendDigestAsync(List<ManagedCertificate> all, List<RenewalReportItem> report, IProgress<string>? log)
    {
        if (_email == null || !_email.Enabled) return;
        try
        {
            var warnDays = _settings.ExpiryWarningDays;
            var expiring = all.Where(c => c.DateExpiry.HasValue && c.DaysUntilExpiry <= warnDays).ToList();
            if (report.Count == 0 && expiring.Count == 0) return;
            if (report.All(r => r.Success) && expiring.Count == 0)
            {
                log?.Report("[Email] Wszystko OK, brak powodów do maila.");
                return;
            }
            var (ok, msg) = await _email.SendDigestAsync(report, expiring, warnDays);
            log?.Report(ok ? "[Email] Digest wyslany." : $"[Email] BLAD wysylki: {msg}");
        }
        catch (Exception ex)
        {
            log?.Report($"[Email] Wyjątek: {ex.Message}");
        }
    }

    /// <summary>Wdraza na wszystkie cele. Zwraca null gdy OK, inaczej opis bledow.</summary>
    public async Task<string?> DeployAsync(ManagedCertificate cert, IProgress<string>? log = null, CancellationToken ct = default)
    {
        var errors = new List<string>();
        foreach (var target in cert.DeploymentTargets)
        {
            var deployer = _deploymentTargets.FirstOrDefault(d => d.TargetType == target.TargetType);
            if (deployer == null)
            {
                log?.Report($"[Deploy] Brak deployera dla {target.TargetType}");
                errors.Add($"{target.TargetType}: brak deployera");
                continue;
            }
            log?.Report($"[Deploy] Wdrażam na {target.TargetType} ...");
            var res = await deployer.DeployAsync(cert, target, log, ct);
            log?.Report(res.Success ? $"[Deploy] {target.TargetType} OK: {res.Message}" : $"[Deploy] {target.TargetType} Błąd: {res.Message}");
            if (!res.Success) errors.Add($"{target.TargetType}: {res.Message}");
        }
        return errors.Count == 0 ? null : string.Join("; ", errors);
    }
}

public interface IDeploymentTarget
{
    DeploymentTargetType TargetType { get; }
    Task<DeploymentResult> DeployAsync(ManagedCertificate cert, DeploymentTarget config, IProgress<string>? log, CancellationToken ct);
    Task<List<string>> DiscoverSitesAsync();
    // Hostnames z bindingow danego site (jak CTW "Select Site" -> identifiers).
    // Domyslnie brak (np. Apache/Nginx bez implementacji).
    Task<List<string>> DiscoverSiteBindingsAsync(string siteName) => Task.FromResult(new List<string>());
}

public class DeploymentResult
{
    public bool Success { get; set; }
    public string Message { get; set; } = string.Empty;
}
