using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using Certify.Core.Models;
using Certify.Core.Services;

namespace Certify.ACME;

/// <summary>Klasyfikacja identyfikatorów: domena vs adres IP (RFC 8738).</summary>
public static class IdentifierClassifier
{
    public static bool IsIp(string value) =>
        IPAddress.TryParse(value.Trim().Trim('[', ']'), out _);

    public static List<(string Value, bool IsIp)> Split(IEnumerable<string> domains) =>
        domains.Select(d => d.Trim()).Where(d => d.Length > 0)
            .Select(d => (d, IsIp(d))).ToList();
}

/// <summary>
/// Wystawianie certyfikatow z identyfikatorami IP (RFC 8738) przez minimalny
/// raw-ACME (Certes 3.0.4 wspiera tylko IdentifierType.Dns).
/// IP zawsze przez http-01 (dns-01 zabronione dla IP), reszta domen wg ChallengeType.
/// Wspierane CA: tylko Let's Encrypt (+staging) - ZeroSSL nie wydaje na IP.
/// Artefakty identyczne jak flow Certes (writer) - deployery dzialaja bez zmian.
/// </summary>
public class IpOrderProcessor(AppSettings settings)
{
    private readonly AppSettings _settings = settings;
    private readonly AcmeAccountManager _accounts = new(settings);

    public async Task<CertificateRequestResult> ProcessAsync(
        ManagedCertificate cert, IProgress<string>? log, CancellationToken ct, bool isRenewal)
    {
        // Sprzatanie challenge'y takze po bledzie/anulowaniu (finally).
        var cleanups = new List<(IChallengeHandler handler, string domain, string token)>();
        try
        {
            if (cert.CertificateAuthority is not (CertificateAuthority.LetsEncrypt or CertificateAuthority.LetsEncryptStaging))
                return Fail(cert, Certify.Core.Localization.UIStrings.T("Acme_Err_IpOnlyLe", cert.CertificateAuthority));

            var ids = IdentifierClassifier.Split(cert.Domains);
            var ips = ids.Where(i => i.IsIp).Select(i => i.Value).ToList();
            log?.Report($"[ACME-IP] Identyfikatory IP: {string.Join(", ", ips)}");

            // IP wymaga http-01 z webroot na tym IP (port 80).
            foreach (var ip in ips)
            {
                if (string.IsNullOrWhiteSpace(ResolveRoot(cert, ip)))
                    return Fail(cert, Certify.Core.Localization.UIStrings.T("Acme_Err_IpWebroot", ip));
            }

            var dirUrl = _settings.GetDirectoryUrl(cert.CertificateAuthority);
            var accountPem = await _accounts.GetOrCreateAccountKeyAsync(cert.EmailAddress, dirUrl);
            using var accountKey = ECDsa.Create();
            accountKey.ImportFromPem(accountPem.ToPem());
            var accountJwk = AcmeJws.BuildEcJwk(accountKey.ExportParameters(false));
            var thumbprint = AcmeJws.Thumbprint(accountJwk);

            using var raw = new RawAcmeClient();
            var dir = await GetDirAsync(raw, dirUrl, log, ct);
            var newNonce = Req(dir, "newNonce");
            var newAccountUrl = Req(dir, "newAccount");
            var newOrderUrl = Req(dir, "newOrder");
            await NonceAsync(raw, newNonce, log, ct);

            // Konto: lookup, potem create w razie potrzeby.
            string accountUrl;
            try
            {
                var lookupPayload = $"{{\"onlyReturnExisting\":true}}";
                var (_, loc) = await PostAsync(raw, newAccountUrl,
                    nonce => AcmeJws.EncodeJws(accountKey, ProtectedJwk(nonce, newAccountUrl, accountJwk), lookupPayload), log, ct, "account lookup");
                accountUrl = loc ?? throw new InvalidOperationException(Certify.Core.Localization.UIStrings.T("Acme_Err_NoLocation"));
                log?.Report("[ACME-IP] Konto istnieje.");
            }
            catch (AcmeException ex) when (ex.StatusCode is 400 or 404)
            {
                log?.Report($"[ACME-IP] Zakładam nowe konto dla {cert.EmailAddress} ...");
                var createPayload = $"{{\"termsOfServiceAgreed\":true,\"contact\":[\"mailto:{AcmeJws.Escape(cert.EmailAddress)}\"]}}";
                var (_, loc) = await PostAsync(raw, newAccountUrl,
                    nonce => AcmeJws.EncodeJws(accountKey, ProtectedJwk(nonce, newAccountUrl, accountJwk), createPayload), log, ct, "account create");
                accountUrl = loc ?? throw new InvalidOperationException(Certify.Core.Localization.UIStrings.T("Acme_Err_NoLocation"));
            }
            string KidProtected(string nonce, string url) =>
                $"{{\"alg\":\"ES256\",\"kid\":\"{AcmeJws.Escape(accountUrl)}\",\"nonce\":\"{nonce}\",\"url\":\"{AcmeJws.Escape(url)}\"}}";

            // newOrder z typami ip/dns.
            var identifiersJson = string.Join(",", ids.Select(i =>
                $"{{\"type\":\"{(i.IsIp ? "ip" : "dns")}\",\"value\":\"{AcmeJws.Escape(i.Value)}\"}}"));
            log?.Report($"[ACME-IP] newOrder ({ids.Count} identyfikatorów) ...");
            var (orderBody, orderUrl) = await PostAsync(raw, newOrderUrl,
                nonce => AcmeJws.EncodeJws(accountKey, KidProtected(nonce, newOrderUrl),
                    $"{{\"identifiers\":[{identifiersJson}]}}"), log, ct, "newOrder");
            if (orderUrl == null) throw new InvalidOperationException(Certify.Core.Localization.UIStrings.T("Acme_Err_NoOrderLocation"));
            var finalizeUrl = Req(orderBody, "finalize");
            var authzUrls = JsonStrings(orderBody, "authorizations");
            log?.Report($"[ACME-IP] Order: {orderUrl}");

            // Challenge per authz: IP -> http-01, DNS -> wg ChallengeType.
            foreach (var authzUrl in authzUrls)
            {
                var (authzBody, _) = await PostAsync(raw, authzUrl,
                    nonce => AcmeJws.EncodeJws(accountKey, KidProtected(nonce, authzUrl), ""), log, ct, "authz");
                var identType = JsonPath(authzBody, "identifier", "type") ?? "dns";
                var identValue = JsonPath(authzBody, "identifier", "value") ?? "?";
                var wantChallenge = identType == "ip" ? "http-01"
                    : cert.ChallengeType == ChallengeType.Dns01 ? "dns-01" : "http-01";

                var (chUrl, token) = FindChallenge(authzBody, wantChallenge);
                var keyAuthz = AcmeJws.KeyAuthorization(token, thumbprint);
                // dns-01: rekord TXT to digest keyAuthorization (RFC 8555 8.4).
                var txtValue = wantChallenge == "dns-01" ? ChallengeHandlerFactory.DnsTxtValue(keyAuthz) : keyAuthz;
                log?.Report($"[ACME-IP] {identValue} ({identType}) -> {wantChallenge}");

                IChallengeHandler handler = wantChallenge == "http-01"
                    ? new Http01FilesystemHandler(d => ResolveRoot(cert, d), Http01FilesystemHandler.SelfCheckClient)
                    : ChallengeHandlerFactory.CreateDns(cert);
                cleanups.Add((handler, identValue, token));
                await handler.PrepareAsync(identValue, token, txtValue, log, ct);

                await PostAsync(raw, chUrl,
                    nonce => AcmeJws.EncodeJws(accountKey, KidProtected(nonce, chUrl), "{}"), log, ct, "challenge respond");
            }

            // Poll authz.
            foreach (var authzUrl in authzUrls)
            {
                await PollAsync($"authz {authzUrl}", ct, async () =>
                {
                    var (b, _) = await PostAsync(raw, authzUrl,
                        nonce => AcmeJws.EncodeJws(accountKey, KidProtected(nonce, authzUrl), ""), log, ct, "authz poll");
                    return JsonStatus(b);
                }, log);
            }

            // CSR z SAN (DNS + IP) i finalize.
            log?.Report("[ACME-IP] Generuje klucz i CSR z SAN ...");
            using var certKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var csrDer = BuildCsr(cert, certKey);
            var (finBody, _) = await PostAsync(raw, finalizeUrl,
                nonce => AcmeJws.EncodeJws(accountKey, KidProtected(nonce, finalizeUrl),
                    $"{{\"csr\":\"{AcmeJws.B64U(csrDer)}\"}}"), log, ct, "finalize");
            log?.Report($"[ACME-IP] finalize status: {JsonStatus(finBody)}");

            string? certificateUrl = null;
            await PollAsync("order", ct, async () =>
            {
                var (b, _) = await PostAsync(raw, orderUrl,
                    nonce => AcmeJws.EncodeJws(accountKey, KidProtected(nonce, orderUrl), ""), log, ct, "order poll");
                certificateUrl = RawAcmeClient.JsonString(b, "certificate");
                return JsonStatus(b);
            }, log);

            log?.Report("[ACME-IP] Pobieram certyfikat ...");
            var (chainPem, _) = await PostAsync(raw, certificateUrl!,
                nonce => AcmeJws.EncodeJws(accountKey, KidProtected(nonce, certificateUrl!), ""), log, ct, "download");
            var blocks = CertificateArtifactWriter.SplitPemBlocks(chainPem);
            if (blocks.Count == 0) throw new InvalidOperationException(Certify.Core.Localization.UIStrings.T("Acme_Err_EmptyChain"));
            var keyPem = PemEncode("PRIVATE KEY", certKey.ExportPkcs8PrivateKey());
            var pfx = CertificateArtifactWriter.BuildPfx(blocks[0], chainPem, certKey, CertificateArtifactWriter.PfxPassword);

            await CertificateArtifactWriter.WriteAsync(_settings.DataDirectory, cert,
                blocks[0] + "\n", chainPem, keyPem, pfx, isRenewal, log, ct);

            return new CertificateRequestResult { Success = true, Message = "OK", Certificate = cert };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            log?.Report($"[ACME-IP] BŁĄD: {ex.Message}");
            cert.Status = CertificateStatus.Error;
            cert.StatusMessage = ex.Message;
            return new CertificateRequestResult { Success = false, Message = ex.Message, Certificate = cert };
        }
        finally
        {
            foreach (var (handler, domain, token) in cleanups)
                try { await handler.CleanupAsync(domain, token, log, CancellationToken.None); } catch { }
        }
    }

    private static string? ResolveRoot(ManagedCertificate cert, string domain)
    {
        if (cert.ChallengeConfig.HttpChallengeRootPaths.TryGetValue(domain, out var p) && !string.IsNullOrWhiteSpace(p)) return p;
        return cert.ChallengeConfig.HttpChallengeRoot;
    }

    private static Task<Dictionary<string, string>> GetDirAsync(RawAcmeClient raw, string url, IProgress<string>? log, CancellationToken ct) =>
        AcmeRetry.ExecuteAsync(c => raw.GetDirectoryAsync(url, c), log, ct, "ACME-IP directory");

    private static Task NonceAsync(RawAcmeClient raw, string url, IProgress<string>? log, CancellationToken ct) =>
        AcmeRetry.ExecuteAsync(c => raw.RefreshNonceAsync(url, c), log, ct, "ACME-IP nonce");

    private static Task<(string Body, string? Location)> PostAsync(RawAcmeClient raw, string url, Func<string, string> build, IProgress<string>? log, CancellationToken ct, string op) =>
        AcmeRetry.ExecuteAsync(c => raw.PostSignedAsync(url, build, c), log, ct, $"ACME-IP {op}");

    private static CertificateRequestResult Fail(ManagedCertificate cert, string message)
    {
        cert.Status = CertificateStatus.Error;
        cert.StatusMessage = message;
        return new CertificateRequestResult { Success = false, Message = message, Certificate = cert };
    }

    private static string ProtectedJwk(string nonce, string url, string jwk) =>
        $"{{\"alg\":\"ES256\",\"jwk\":{jwk},\"nonce\":\"{nonce}\",\"url\":\"{AcmeJws.Escape(url)}\"}}";

    private static string Req(Dictionary<string, string> dir, string key) =>
        dir.TryGetValue(key, out var v) ? v : throw new InvalidOperationException($"Directory bez '{key}'.");

    private static string Req(string json, string prop) =>
        RawAcmeClient.JsonString(json, prop) ?? throw new InvalidOperationException($"Order bez '{prop}'.");

    private static List<string> JsonStrings(string json, string prop)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty(prop, out var arr) && arr.ValueKind == JsonValueKind.Array)
                return arr.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String)
                    .Select(e => e.GetString()!).ToList();
        }
        catch { }
        return [];
    }

    private static string? JsonPath(string json, params string[] path)
    {
        try
        {
            var el = JsonDocument.Parse(json).RootElement;
            foreach (var p in path)
            {
                if (el.ValueKind != JsonValueKind.Object || !el.TryGetProperty(p, out el)) return null;
            }
            return el.ValueKind == JsonValueKind.String ? el.GetString() : el.ToString();
        }
        catch { return null; }
    }

    private static string JsonStatus(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.TryGetProperty("status", out var s) ? s.GetString() ?? "?" : "?";
        }
        catch { return "?"; }
    }

    private static (string url, string token) FindChallenge(string authzBody, string wantType)
    {
        try
        {
            using var doc = JsonDocument.Parse(authzBody);
            foreach (var ch in doc.RootElement.GetProperty("challenges").EnumerateArray())
            {
                if (ch.GetProperty("type").GetString() == wantType)
                    return (ch.GetProperty("url").GetString()!, ch.GetProperty("token").GetString()!);
            }
        }
        catch { }
        throw new InvalidOperationException(Certify.Core.Localization.UIStrings.T("Acme_Err_NoChallengeInAuthz", wantType));
    }

    private static async Task PollAsync(string what, CancellationToken ct, Func<Task<string>> getStatus, IProgress<string>? log)
    {
        for (var i = 0; i < 30; i++)
        {
            await Task.Delay(2000, ct);
            var status = await getStatus();
            log?.Report($"[ACME-IP] {what} status: {status} ({i + 1})");
            if (status == "valid") return;
            if (status is "invalid" or "revoked" or "deactivated")
                throw new InvalidOperationException(Certify.Core.Localization.UIStrings.T("Acme_Err_BadStatus", what, status));
        }
        throw new TimeoutException(Certify.Core.Localization.UIStrings.T("Acme_Err_PollTimeout", what));
    }

    public static byte[] BuildCsr(ManagedCertificate cert, ECDsa key)
    {
        var req = new CertificateRequest(
            new X500DistinguishedName($"CN={cert.PrimaryDomain}"), key, HashAlgorithmName.SHA256);
        var san = new SubjectAlternativeNameBuilder();
        foreach (var (value, isIp) in IdentifierClassifier.Split(cert.Domains))
        {
            if (isIp) san.AddIpAddress(IPAddress.Parse(value.Trim().Trim('[', ']')));
            else san.AddDnsName(value);
        }
        req.CertificateExtensions.Add(san.Build());
        return req.CreateSigningRequest();
    }

    public static string PemEncode(string label, byte[] der) =>
        $"-----BEGIN {label}-----\n{Convert.ToBase64String(der, Base64FormattingOptions.InsertLineBreaks)}\n-----END {label}-----\n";
}
