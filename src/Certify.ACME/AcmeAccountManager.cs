using System.Text.Json;
using Certes;
using Certes.Acme;
using Certify.Core.Models;

namespace Certify.ACME;

public class AcmeAccountManager
{
    private readonly AppSettings _settings;
    private readonly JsonSerializerOptions _jsonOptions = new() { WriteIndented = true };

    public AcmeAccountManager(AppSettings settings) => _settings = settings;

    private string AccountsPath => _settings.AccountsJsonPath;

    public async Task<IKey> GetOrCreateAccountKeyAsync(string email, string directoryUrl)
    {
        System.IO.Directory.CreateDirectory(Path.GetDirectoryName(AccountsPath)!);
        Dictionary<string, string> accounts = new();
        if (File.Exists(AccountsPath))
        {
            try
            {
                var json = await File.ReadAllTextAsync(AccountsPath);
                accounts = JsonSerializer.Deserialize<Dictionary<string, string>>(json) ?? new();
            }
            catch { }
        }

        var key = $"{directoryUrl}|{email.ToLowerInvariant()}";
        if (accounts.TryGetValue(key, out var pem) && !string.IsNullOrWhiteSpace(pem))
        {
            try { return KeyFactory.FromPem(pem); } catch { }
        }

        var newKey = KeyFactory.NewKey(KeyAlgorithm.ES256);
        accounts[key] = newKey.ToPem();
        await File.WriteAllTextAsync(AccountsPath, JsonSerializer.Serialize(accounts, _jsonOptions));
        return newKey;
    }

    public async Task<AcmeContext> GetAcmeContextAsync(
        string email,
        CertificateAuthority ca,
        IProgress<string>? log = null,
        string? eabKeyId = null,
        string? eabHmacKey = null,
        string? customDirectoryUrl = null,
        CancellationToken ct = default)
    {
        var dirUrl = _settings.GetDirectoryUrl(ca, customDirectoryUrl);
        log?.Report($"[ACME] Directory: {dirUrl}");
        var accountKey = await GetOrCreateAccountKeyAsync(email, dirUrl);
        var ctx = new AcmeContext(new Uri(dirUrl), accountKey);
        try
        {
            var accCtx = await ctx.Account();
            var res = await accCtx.Resource();
            log?.Report($"[ACME] Konto status: {res.Status}");
        }
        catch
        {
            var needsEab = CertificateAuthorityCatalog.RequiresEab(ca);
            if (needsEab && (string.IsNullOrWhiteSpace(eabKeyId) || string.IsNullOrWhiteSpace(eabHmacKey)))
                throw new InvalidOperationException(
                    Certify.Core.Localization.UIStrings.T("Acme_Err_Eab", CertificateAuthorityCatalog.DisplayName(ca)));
            log?.Report($"[ACME] Zakładam nowe konto dla {email} ..." + (needsEab ? " (z EAB)" : ""));
            if (needsEab)
                await AcmeRetry.ExecuteAsync(_ => ctx.NewAccount([$"{email}".StartsWith("mailto:") ? email : $"mailto:{email}"], true, eabKeyId!, eabHmacKey!, "HS256"), log, ct, "newAccount");
            else
                await AcmeRetry.ExecuteAsync(_ => ctx.NewAccount(email, true), log, ct, "newAccount");
            log?.Report("[ACME] Konto utworzone.");
        }
        return ctx;
    }
}
