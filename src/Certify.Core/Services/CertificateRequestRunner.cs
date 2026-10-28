using System.Security.Cryptography.X509Certificates;
using Certify.Core.Localization;
using Certify.Core.Models;

namespace Certify.Core.Services;

public enum RequestResult { Issued, Valid, Failed, Busy, DeployFailed }

public sealed record RequestOutcome(RequestResult Result, string Host, DateTime? Expiry, string? Thumbprint, string Message)
{
    /// <summary>Kod wyjscia polecenia --request.</summary>
    public int ExitCode => Result switch
    {
        RequestResult.Issued or RequestResult.Valid => 0,
        RequestResult.Failed => 1,
        RequestResult.Busy => 3,
        RequestResult.DeployFailed => 5,
        _ => 1
    };
}

/// <summary>
/// Bezokienkowe zamowienie certyfikatu dla hosta na podstawie szablonu (polecenie --request).
/// Sekwencja jak MainWindow.RequestCert_Click, bez e-maili, pod RenewalService.OperationLock.
/// </summary>
public sealed class CertificateRequestRunner
{
    private readonly CertificateStore _store;
    private readonly ICertificateAuthorityProvider _ca;
    private readonly RenewalService _renewal;

    public CertificateRequestRunner(CertificateStore store, ICertificateAuthorityProvider ca, RenewalService renewal)
    {
        _store = store;
        _ca = ca;
        _renewal = renewal;
    }

    /// <summary>Strefa = ostatnie dwie etykiety hosta (x.api.appit.pl -> appit.pl).</summary>
    private static string Zone(string host)
    {
        var labels = host.Trim().TrimEnd('.').Split('.');
        return labels.Length <= 2 ? string.Join('.', labels) : string.Join('.', labels[^2..]);
    }

    private static DateTime? Stamp(ManagedCertificate c) => c.DateRenewed ?? c.DateCreated;

    public static ManagedCertificate? FindTemplate(IEnumerable<ManagedCertificate> all, string host, string? templateDomain)
    {
        if (!string.IsNullOrWhiteSpace(templateDomain))
        {
            var wanted = templateDomain.Trim();
            return all.FirstOrDefault(c => c.Domains.Any(d => d.Equals(wanted, StringComparison.OrdinalIgnoreCase)));
        }
        var suffix = "." + Zone(host);
        return all
            .Where(c => c.Status == CertificateStatus.Valid && !c.IsExpired
                        && c.CertificateAuthority == CertificateAuthority.ZeroSsl
                        && c.ChallengeType == ChallengeType.Http01
                        && c.PrimaryDomain.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(Stamp)
            .FirstOrDefault();
    }

    public static ManagedCertificate BuildFromTemplate(ManagedCertificate template, string host, string site, string webroot)
    {
        var cert = new ManagedCertificate
        {
            Name = host,
            Domains = [host],
            CertificateAuthority = template.CertificateAuthority,
            EmailAddress = template.EmailAddress,
            EabKeyId = template.EabKeyId,
            EabHmacKey = template.EabHmacKey,
            CustomAcmeDirectoryUrl = template.CustomAcmeDirectoryUrl,
            ChallengeType = template.ChallengeType,
            RenewalDaysBeforeExpiry = template.RenewalDaysBeforeExpiry,
            RenewalMode = RenewalMode.Auto,
            DeploymentTargets = [new DeploymentTarget { TargetType = DeploymentTargetType.IIS, SiteId = site }]
        };
        cert.ChallengeConfig.HttpChallengeRoot = webroot;
        cert.ChallengeConfig.HttpChallengeRootPaths[host] = webroot;
        return cert;
    }

    public async Task<RequestOutcome> RunAsync(string host, string site, string webroot, string? templateDomain,
        TimeSpan lockTimeout, IProgress<string>? log, CancellationToken ct)
    {
        var ms = (int)Math.Min(int.MaxValue, Math.Max(0, lockTimeout.TotalMilliseconds));
        if (!await _renewal.OperationLock.WaitAsync(ms, ct))
        {
            const string busy = "Trwa inna operacja na certyfikatach (GUI, usługa albo --renew) — spróbuj później.";
            log?.Report("[Request] " + busy);
            return new RequestOutcome(RequestResult.Busy, host, null, null, busy);
        }
        try
        {
            return await RunLockedAsync(host, site, webroot, templateDomain, log, ct);
        }
        finally
        {
            _renewal.OperationLock.Release();
        }
    }

    private async Task<RequestOutcome> RunLockedAsync(string host, string site, string webroot, string? templateDomain,
        IProgress<string>? log, CancellationToken ct)
    {
        var all = await _store.LoadAllAsync();
        var existing = all.FirstOrDefault(c => c.Domains.Any(d => d.Equals(host, StringComparison.OrdinalIgnoreCase)));

        // Idempotencja: wazny certyfikat z plikiem PFX tylko wdrazamy, bez nowego zamowienia.
        if (existing != null && existing.Status == CertificateStatus.Valid && !existing.IsExpired
            && !string.IsNullOrWhiteSpace(existing.PfxPath) && File.Exists(existing.PfxPath))
        {
            log?.Report($"[Request] {host}: certyfikat jest już ważny, wdrażam bez nowego zamówienia.");
            try
            {
                var err = await _renewal.DeployAsync(existing, log, ct);
                CertificateHistoryLog.Add(existing, HistoryKind.Deploy, err == null, err ?? UIStrings.T("Hist_Msg_Deployed"));
                if (err != null) existing.StatusMessage = UIStrings.T("Deploy_Msg_Failed", err);
                await _store.UpsertAsync(existing);
                return err == null
                    ? Outcome(RequestResult.Valid, host, existing, "Certyfikat był już ważny, wdrożenie potwierdzone.")
                    : Outcome(RequestResult.DeployFailed, host, existing, "Certyfikat ważny, ale wdrożenie się nie powiodło: " + err);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                log?.Report($"[Request] Wyjątek: {ex.Message}");
                return new RequestOutcome(RequestResult.Failed, host, null, null, ex.Message);
            }
        }

        ManagedCertificate cert;
        if (existing != null)
        {
            // Nieważny: nowe zamówienie na tym samym rekordzie (to samo Id).
            cert = existing;
            log?.Report($"[Request] {host}: istniejący certyfikat nieważny, ponawiam zamówienie na tym samym rekordzie.");
            cert.ChallengeConfig.HttpChallengeRoot ??= webroot;
            if (!cert.ChallengeConfig.HttpChallengeRootPaths.ContainsKey(host))
                cert.ChallengeConfig.HttpChallengeRootPaths[host] = webroot;
            if (!cert.DeploymentTargets.Any(t => t.TargetType == DeploymentTargetType.IIS
                    && string.Equals(t.SiteId, site, StringComparison.OrdinalIgnoreCase)))
                cert.DeploymentTargets.Add(new DeploymentTarget { TargetType = DeploymentTargetType.IIS, SiteId = site });
        }
        else
        {
            var template = FindTemplate(all, host, templateDomain);
            if (template == null)
            {
                var msg = string.IsNullOrWhiteSpace(templateDomain)
                    ? $"Brak szablonu: nie znaleziono ważnego certyfikatu ZeroSSL (http-01) w strefie {Zone(host)}. Podaj --template."
                    : $"Brak szablonu: nie znaleziono certyfikatu z domeną {templateDomain}.";
                log?.Report("[Request] " + msg);
                return new RequestOutcome(RequestResult.Failed, host, null, null, msg);
            }
            log?.Report($"[Request] Szablon: {template.Name} ({template.PrimaryDomain}).");
            cert = BuildFromTemplate(template, host, site, webroot);
        }

        try
        {
            log?.Report($"[Request] === REQUEST dla {cert.Name} ===");
            cert.LastAttemptedCa = cert.CertificateAuthority.ToString();
            var tasks = new CertificateTaskRunner();
            if (!await tasks.RunPreRequestTasksAsync(cert, log, ct))
            {
                cert.Status = CertificateStatus.Error;
                cert.StatusMessage = UIStrings.T("Renew_Msg_PreFail");
                CertificateHistoryLog.Add(cert, HistoryKind.Request, false, cert.StatusMessage);
                await _store.UpsertAsync(cert);
                log?.Report("[Request] PRZERWANO: pre-request task nie powiódł się.");
                return new RequestOutcome(RequestResult.Failed, host, null, null, cert.StatusMessage);
            }

            var result = await _ca.RequestCertificateAsync(cert, log, ct);
            if (!result.Success)
            {
                await tasks.RunDeploymentTasksAsync(cert, certSuccess: false, log, ct);
                cert.Status = CertificateStatus.Error;
                cert.StatusMessage = result.Message;
                CertificateHistoryLog.Add(cert, HistoryKind.Request, false, result.Message);
                await _store.UpsertAsync(cert);
                log?.Report($"[Request] BŁĄD: {result.Message}");
                return new RequestOutcome(RequestResult.Failed, host, null, null, result.Message);
            }

            log?.Report("[Request] Sukces, wdrażam.");
            CertificateHistoryLog.Add(cert, HistoryKind.Request, true, UIStrings.T("Hist_Msg_Requested"));
            var deployError = await _renewal.DeployAsync(cert, log, ct);
            if (deployError != null)
            {
                cert.StatusMessage = UIStrings.T("Deploy_Msg_Failed", deployError);
                CertificateHistoryLog.Add(cert, HistoryKind.Deploy, false, deployError);
            }
            await tasks.RunDeploymentTasksAsync(cert, certSuccess: true, log, ct);
            await _store.UpsertAsync(cert);
            log?.Report(deployError == null ? "[Request] Zakończono pomyślnie." : $"[Request] Certyfikat wystawiony, BŁĄD wdrożenia: {deployError}");
            return deployError == null
                ? Outcome(RequestResult.Issued, host, cert, "Certyfikat wystawiony i wdrożony.")
                : Outcome(RequestResult.DeployFailed, host, cert, "Certyfikat wystawiony i zapisany, ale wdrożenie się nie powiodło: " + deployError);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            cert.Status = CertificateStatus.Error;
            cert.StatusMessage = ex.Message;
            CertificateHistoryLog.Add(cert, HistoryKind.Request, false, ex.Message);
            await _store.UpsertAsync(cert);
            log?.Report($"[Request] Wyjątek: {ex.Message}");
            return new RequestOutcome(RequestResult.Failed, host, null, null, ex.Message);
        }
    }

    private static RequestOutcome Outcome(RequestResult result, string host, ManagedCertificate cert, string message) =>
        new(result, host, cert.DateExpiry, Thumbprint(cert), message);

    /// <summary>Odcisk palca z PFX (haslo PfxPassword ?? "certify"); null gdy sie nie da.</summary>
    private static string? Thumbprint(ManagedCertificate cert)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(cert.PfxPath) || !File.Exists(cert.PfxPath)) return null;
            using var x = new X509Certificate2(cert.PfxPath, cert.PfxPassword ?? "certify");
            return x.Thumbprint;
        }
        catch { return null; }
    }
}
