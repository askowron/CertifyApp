using System.Security.Cryptography.X509Certificates;
using Certes;
using Certes.Acme;
using Certes.Acme.Resource;
using Certify.Core.Models;
using Certify.Core.Services;

namespace Certify.ACME;

public class LetsEncryptService : ICertificateAuthorityProvider
{
    private readonly AppSettings _settings;
    private readonly AcmeAccountManager _accountManager;

    public LetsEncryptService(AppSettings settings)
    {
        _settings = settings;
        _accountManager = new AcmeAccountManager(settings);
    }

    public Task<CertificateRequestResult> RequestCertificateAsync(ManagedCertificate managedCert, IProgress<string>? log = null, CancellationToken ct = default)
        => ProcessOrderAsync(managedCert, log, ct, isRenewal: false);

    public Task<CertificateRequestResult> RenewCertificateAsync(ManagedCertificate managedCert, IProgress<string>? log = null, CancellationToken ct = default)
        => ProcessOrderAsync(managedCert, log, ct, isRenewal: true);

    private async Task<CertificateRequestResult> ProcessOrderAsync(ManagedCertificate managedCert, IProgress<string>? log, CancellationToken ct, bool isRenewal)
    {
        if (!managedCert.Domains.Any())
            return new CertificateRequestResult { Success = false, Message = Certify.Core.Localization.UIStrings.T("Acme_Err_NoDomains") };
        if (string.IsNullOrWhiteSpace(managedCert.EmailAddress))
            return new CertificateRequestResult { Success = false, Message = Certify.Core.Localization.UIStrings.T("Acme_Err_NoEmail") };

        // Sprzatanie challenge'y takze po bledzie/anulowaniu (wczesniej tylko po sukcesie
        // - zostawaly pliki w webroot i rekordy TXT w DNS).
        IChallengeHandler? challengeHandler = null;
        var prepared = new List<(string Domain, string Token)>();
        try
        {
            log?.Report($"[ACME] Start zamówienie dla: {string.Join(", ", managedCert.Domains)} CA={managedCert.CertificateAuthority} Challenge={managedCert.ChallengeType}");
            // Identyfikatory IP (RFC 8738) ida raw-ACME (Certes wspiera tylko DNS).
            if (managedCert.Domains.Any(IdentifierClassifier.IsIp))
            {
                log?.Report("[ACME] Wykryto adresy IP - używam flow RFC 8738.");
                return await new IpOrderProcessor(_settings).ProcessAsync(managedCert, log, ct, isRenewal);
            }
            await EnsureZeroSslEabAsync(managedCert, log, ct);
            var maxPoll = CertificateAuthorityCatalog.MaxPollTime(managedCert.CertificateAuthority);

            var acme = await _accountManager.GetAcmeContextAsync(
                managedCert.EmailAddress,
                managedCert.CertificateAuthority,
                log,
                managedCert.EabKeyId,
                managedCert.EabHmacKey,
                managedCert.CustomAcmeDirectoryUrl,
                ct);

            string? ResolveRoot(string domain)
            {
                if (managedCert.ChallengeConfig.HttpChallengeRootPaths.TryGetValue(domain, out var p) && !string.IsNullOrWhiteSpace(p)) return p;
                return managedCert.ChallengeConfig.HttpChallengeRoot;
            }

            challengeHandler = managedCert.ChallengeType switch
            {
                ChallengeType.Http01 => new Http01FilesystemHandler(ResolveRoot, Http01FilesystemHandler.SelfCheckClient),
                ChallengeType.Dns01 => ChallengeHandlerFactory.CreateDns(managedCert),
                _ => throw new NotSupportedException($"Challenge {managedCert.ChallengeType} nieobsługiwany")
            };

            var order = await AcmeRetry.ExecuteAsync(_ => acme.NewOrder(managedCert.Domains), log, ct, "newOrder");
            log?.Report($"[ACME] Zamówienie utworzone: {order.Location}");

            var authorizations = await AcmeRetry.ExecuteAsync(_ => order.Authorizations(), log, ct, "authorizations");
            var challengeTuples = new List<(string domain, IChallengeContext challenge, string token, string keyAuthz)>();

            foreach (var authz in authorizations)
            {
                var authRes = await authz.Resource();
                var domain = authRes.Identifier.Value!;
                log?.Report($"[ACME] Autoryzacja dla {domain} status={authRes.Status}");
                IChallengeContext? challengeCtx = null;
                string? token = null, keyAuthz = null;

                if (managedCert.ChallengeType == ChallengeType.Http01)
                {
                    var http = await AcmeRetry.ExecuteAsync(_ => authz.Http(), log, ct, $"challenge {domain}");
                    challengeCtx = http;
                    token = http.Token;
                    keyAuthz = http.KeyAuthz;
                }
                else if (managedCert.ChallengeType == ChallengeType.Dns01)
                {
                    var dns = await AcmeRetry.ExecuteAsync(_ => authz.Dns(), log, ct, $"challenge {domain}");
                    challengeCtx = dns;
                    token = dns.Token;
                    // Rekord TXT to base64url(SHA256(keyAuthorization)) (RFC 8555 8.4).
                    // Certes 3.0.4 nie ma DnsTxt na challenge'u - wczesniej szedl surowy
                    // keyAuthz i kazda walidacja dns-01 konczyla sie bledem.
                    keyAuthz = ChallengeHandlerFactory.DnsTxtValue(dns.KeyAuthz);
                }

                if (challengeCtx == null || token == null || keyAuthz == null)
                    throw new Exception(Certify.Core.Localization.UIStrings.T("Acme_Err_NoChallenge", domain));

                prepared.Add((domain, token));
                await challengeHandler.PrepareAsync(domain, token, keyAuthz, log, ct);
                challengeTuples.Add((domain, challengeCtx, token, keyAuthz));
            }

            foreach (var (domain, challenge, _, _) in challengeTuples)
            {
                log?.Report($"[ACME] Waliduję challenge dla {domain} ...");
                await AcmeRetry.ExecuteAsync(_ => challenge.Validate(), log, ct, $"validate {domain}");
            }

            foreach (var (domain, challenge, _, _) in challengeTuples)
            {
                var retries = 0;
                var deadline = DateTime.UtcNow + maxPoll;
                ChallengeStatus status;
                do
                {
                    await Task.Delay(PollDelay(retries), ct);
                    var chRes = await AcmeRetry.ExecuteAsync(_ => challenge.Resource(), log, ct, $"challenge status {domain}");
                    status = chRes.Status ?? ChallengeStatus.Pending;
                    // Niektore CA (ZeroSSL) podaja blad ostatniej proby jeszcze w trakcie "processing".
                    var detail = status != ChallengeStatus.Invalid && chRes.Error != null ? $" - {chRes.Error.Type}: {chRes.Error.Detail}" : "";
                    log?.Report($"[ACME] {domain} challenge status: {status} ({++retries}){detail}");
                    if (status == ChallengeStatus.Valid) break;
                    if (status == ChallengeStatus.Invalid)
                    {
                        var err = chRes.Error != null ? $"{chRes.Error.Type}: {chRes.Error.Detail}" : "invalid";
                        throw new Exception(Certify.Core.Localization.UIStrings.T("Acme_Err_VerifyFail", domain, err));
                    }
                    if (DateTime.UtcNow > deadline) throw new TimeoutException(Certify.Core.Localization.UIStrings.T("Acme_Err_ValTimeout", domain));
                } while (status == ChallengeStatus.Pending || status == ChallengeStatus.Processing);
            }

            // Order musi byc "ready" przed finalize - ZeroSSL po walidacji challenge'y
            // bywa jeszcze chwile "pending", a finalize wtedy konczy sie orderNotReady.
            await WaitOrderAsync(order, OrderStatus.Ready, maxPoll, log, ct);

            log?.Report("[ACME] Generuję klucz i CSR ...");
            var privateKey = KeyFactory.NewKey(KeyAlgorithm.ES256);

            // Sam Finalize zamiast Generate: Generate czeka na "processing" tylko raz
            // (retryCount=1) i rzuca bledem, a ZeroSSL potrafi wystawiac kilka minut.
            await AcmeRetry.ExecuteAsync(_ => order.Finalize(new CsrInfo
            {
                CommonName = managedCert.PrimaryDomain,
            }, privateKey), log, ct, "finalize");

            await WaitOrderAsync(order, OrderStatus.Valid, maxPoll, log, ct);

            log?.Report("[ACME] Pobieram certyfikat ...");
            var certChain = await AcmeRetry.ExecuteAsync(_ => order.Download(), log, ct, "download");

            var (leafPem, chainPem, pfxBytes) = BuildArtifacts(certChain, privateKey);

            await CertificateArtifactWriter.WriteAsync(
                _settings.DataDirectory, managedCert,
                leafPem,
                chainPem,
                privateKey.ToPem(),
                pfxBytes, isRenewal, log, ct);

            return new CertificateRequestResult { Success = true, Message = "OK", Certificate = managedCert };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Anulowanie przez uzytkownika - nie oznaczaj certu jako Error.
            throw;
        }
        catch (Exception ex)
        {
            log?.Report($"[ACME] BŁĄD: {ex}");
            managedCert.Status = CertificateStatus.Error;
            managedCert.StatusMessage = ex.Message;
            return new CertificateRequestResult { Success = false, Message = ex.Message, Certificate = managedCert };
        }
        finally
        {
            if (challengeHandler != null)
            {
                foreach (var (domain, token) in prepared)
                {
                    try { await challengeHandler.CleanupAsync(domain, token, log, CancellationToken.None); }
                    catch (Exception ex) { log?.Report($"[ACME] Cleanup warn {domain}: {ex.Message}"); }
                }
            }
        }
    }

    /// <summary>
    /// ZeroSSL bez wpisanego EAB: pobierz EAB z API dla emaila i zapamietaj na certyfikacie
    /// (zapisywany po operacji), zeby nie pytac API przy kazdym odnowieniu.
    /// </summary>
    private static async Task EnsureZeroSslEabAsync(ManagedCertificate cert, IProgress<string>? log, CancellationToken ct)
    {
        if (!CertificateAuthorityCatalog.CanAutoFetchEab(cert.CertificateAuthority)) return;
        if (!string.IsNullOrWhiteSpace(cert.EabKeyId) && !string.IsNullOrWhiteSpace(cert.EabHmacKey)) return;
        log?.Report($"[ACME] ZeroSSL: brak EAB - pobieram automatycznie dla {cert.EmailAddress} ...");
        try
        {
            var (kid, hmac) = await AcmeRetry.ExecuteAsync(c => ZeroSslEab.FetchByEmailAsync(cert.EmailAddress, c), log, ct, "ZeroSSL EAB");
            cert.EabKeyId = kid;
            cert.EabHmacKey = hmac;
            log?.Report($"[ACME] ZeroSSL: EAB pobrany (KID {kid}).");
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            throw new InvalidOperationException(
                Certify.Core.Localization.UIStrings.T("Acme_Err_ZeroSslEab", cert.EmailAddress, ex.Message), ex);
        }
    }

    /// <summary>
    /// Leaf PEM, chain PEM i PFX z lancucha pobranego od CA - dokladnie w postaci od CA.
    /// NIE uzywac CertificateChain.ToPem()/ToPfx() z Certes: oba szukaja wystawcow na
    /// wbudowanej liscie Certes i odrzucaly lancuch ZeroSSL (Sectigo Root E46
    /// cross-signed przez USERTrust ECC, ktorego Certes nie zna).
    /// </summary>
    public static (string LeafPem, string ChainPem, byte[] Pfx) BuildArtifacts(CertificateChain chain, IKey certKey)
    {
        var leafPem = chain.Certificate.ToPem();
        var chainPem = string.Join("\n", new[] { leafPem }.Concat(chain.Issuers.Select(i => i.ToPem())).Select(p => p.Trim())) + "\n";
        return (leafPem, chainPem, BuildPfx(certKey, leafPem, chainPem));
    }

    /// <summary>PFX czystym .NET (jak flow IP), bez weryfikacji lancucha przez Certes.</summary>
    public static byte[] BuildPfx(IKey certKey, string leafPem, string chainPem)
    {
        using var ecdsa = System.Security.Cryptography.ECDsa.Create();
        ecdsa.ImportFromPem(certKey.ToPem());
        return CertificateArtifactWriter.BuildPfx(leafPem, chainPem, ecdsa, CertificateArtifactWriter.PfxPassword);
    }

    /// <summary>2s na start, potem do 10s - szybkie CA (LE) bez zmian, wolne bez zasypywania API.</summary>
    private static TimeSpan PollDelay(int attempt) => TimeSpan.FromSeconds(Math.Min(2 + attempt, 10));

    private static async Task WaitOrderAsync(IOrderContext order, OrderStatus want, TimeSpan maxPoll, IProgress<string>? log, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + maxPoll;
        for (var attempt = 0; ; attempt++)
        {
            var ordRes = await AcmeRetry.ExecuteAsync(_ => order.Resource(), log, ct, "order status");
            var status = ordRes.Status ?? OrderStatus.Pending;
            log?.Report($"[ACME] Order status: {status}");
            if (status == want || status == OrderStatus.Valid) return;
            if (status == OrderStatus.Invalid)
                throw new Exception(Certify.Core.Localization.UIStrings.T("Acme_Err_OrderInvalid", ordRes.Error ?? "invalid"));
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException(Certify.Core.Localization.UIStrings.T("Acme_Err_OrderTimeout"));
            await Task.Delay(PollDelay(attempt), ct);
        }
    }
}
