using Certes.Acme.Resource;
using Certify.Core.Models;

namespace Certify.ACME;

public interface IChallengeHandler
{
    Task PrepareAsync(string domain, string token, string keyAuthz, IProgress<string>? log, CancellationToken ct);
    Task CleanupAsync(string domain, string token, IProgress<string>? log, CancellationToken ct);
}

public class Http01FilesystemHandler : IChallengeHandler
{
    private readonly Func<string, string?> _resolveWebRoot;
    private readonly HttpClient? _selfCheckHttp;

    /// <summary>Wspolny klient self-checku dla zamowien (null w konstruktorze = bez self-checku, np. testy).</summary>
    public static readonly HttpClient SelfCheckClient = new() { Timeout = TimeSpan.FromSeconds(10) };

    public Http01FilesystemHandler(Func<string, string?> resolveWebRoot, HttpClient? selfCheckHttp = null)
    {
        _resolveWebRoot = resolveWebRoot;
        _selfCheckHttp = selfCheckHttp;
    }

    public Task PrepareAsync(string domain, string token, string keyAuthz, IProgress<string>? log, CancellationToken ct)
    {
        var root = _resolveWebRoot(domain);
        if (string.IsNullOrWhiteSpace(root))
            throw new InvalidOperationException($"Brak skonfigurowanej ścieżki webroot dla domeny {domain}. Ustaw ChallengeConfig.HttpChallengeRoot lub mapę domen.");

        var challengeDir = Path.Combine(root!, ".well-known", "acme-challenge");
        var filePath = Path.Combine(challengeDir, token);
        try
        {
            System.IO.Directory.CreateDirectory(challengeDir);
            EnsureIisWebConfig(challengeDir, log);
            File.WriteAllText(filePath, keyAuthz);
        }
        catch (UnauthorizedAccessException ex)
        {
            // Domyslne ACL C:\inetpub\wwwroot: Users tylko odczyt, Administratorzy pelne - ale tylko z elewacja.
            throw new UnauthorizedAccessException(Certify.Core.Localization.UIStrings.T("Acme_Err_WebrootAccess",
                root, IsElevated() ? "" : Certify.Core.Localization.UIStrings.T("Acme_Hint_NotElevated"), Environment.UserName), ex);
        }
        log?.Report($"[HTTP-01] Zapisano challenge dla {domain} -> {filePath}");
        return _selfCheckHttp == null ? Task.CompletedTask : SelfCheckAsync(_selfCheckHttp, domain, token, keyAuthz, log, ct);
    }

    /// <summary>
    /// Pobiera token tak jak CA (http://domena/.well-known/acme-challenge/token). Tylko diagnostyka:
    /// ZeroSSL przy nieosiagalnym pliku trzyma challenge w "processing" przez wiele minut bez bledu,
    /// wiec bez tego log nie mowi, czy problem jest po stronie serwera. Nie przerywa zamowienia -
    /// z wnetrza sieci (NAT bez hairpin, split DNS) wynik moze sie roznic od widoku CA.
    /// </summary>
    internal static async Task SelfCheckAsync(HttpClient http, string domain, string token, string keyAuthz, IProgress<string>? log, CancellationToken ct)
    {
        var host = System.Net.IPAddress.TryParse(domain, out var ip) && ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6
            ? $"[{domain}]" : domain;
        var url = $"http://{host}/.well-known/acme-challenge/{token}";
        try
        {
            using var resp = await http.GetAsync(url, ct);
            var body = (await resp.Content.ReadAsStringAsync(ct)).Trim();
            if (resp.IsSuccessStatusCode && body == keyAuthz)
                log?.Report($"[HTTP-01] Self-check OK: {url}");
            else if (resp.IsSuccessStatusCode)
                log?.Report($"[HTTP-01] UWAGA: self-check {url} -> HTTP {(int)resp.StatusCode}, ale treść inna niż token " +
                            $"(webroot nie jest katalogiem strony obsługującej {domain}?).");
            else
                log?.Report($"[HTTP-01] UWAGA: self-check {url} -> HTTP {(int)resp.StatusCode} {resp.ReasonPhrase}. " +
                            "CA prawdopodobnie też nie pobierze pliku - sprawdź webroot (ścieżka fizyczna strony w IIS) i reguły przekierowań.");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            log?.Report($"[HTTP-01] UWAGA: self-check {url} nieudany: {ex.GetBaseException().Message} " +
                        "(z wnętrza sieci może to być brak NAT hairpin - wtedy liczy się widok CA).");
        }
    }

    public Task CleanupAsync(string domain, string token, IProgress<string>? log, CancellationToken ct)
    {
        try
        {
            var root = _resolveWebRoot(domain);
            if (root != null)
            {
                var filePath = Path.Combine(root, ".well-known", "acme-challenge", token);
                // Loguj tylko faktyczne usuniecie (wczesniej "Usunieto" takze gdy pliku nigdy nie bylo).
                if (File.Exists(filePath))
                {
                    File.Delete(filePath);
                    log?.Report($"[HTTP-01] Usunięto challenge file {filePath}");
                }
            }
        }
        catch (Exception ex) { log?.Report($"[HTTP-01] Cleanup warn: {ex.Message}"); }
        return Task.CompletedTask;
    }

    /// <summary>
    /// web.config dla IIS w katalogu challenge (jak Certify The Web): IIS domyslnie nie serwuje
    /// plikow bez rozszerzenia (token) -> 404 i nieudana walidacja. Tylko gdy pliku brak -
    /// nie nadpisujemy konfiguracji uzytkownika. Dla Apache/Nginx plik jest ignorowany.
    /// </summary>
    public static void EnsureIisWebConfig(string challengeDir, IProgress<string>? log)
    {
        var path = Path.Combine(challengeDir, "web.config");
        if (File.Exists(path)) return;
        File.WriteAllText(path, IisChallengeWebConfig);
        log?.Report($"[HTTP-01] Utworzono {path} (IIS: serwowanie plików bez rozszerzenia).");
    }

    // <remove> przed <mimeMap>: gdy nadrzedny config ma juz ".", sam <mimeMap> dalby blad 500 (duplikat).
    public const string IisChallengeWebConfig = """
        <?xml version="1.0" encoding="UTF-8"?>
        <configuration>
          <system.webServer>
            <staticContent>
              <remove fileExtension="." />
              <mimeMap fileExtension="." mimeType="text/plain" />
            </staticContent>
            <handlers>
              <clear />
              <add name="StaticFile" path="*" verb="*" modules="StaticFileModule" resourceType="Either" requireAccess="Read" />
            </handlers>
          </system.webServer>
        </configuration>
        """;

    private static bool IsElevated()
    {
        if (!OperatingSystem.IsWindows()) return false;
        try
        {
            using var id = System.Security.Principal.WindowsIdentity.GetCurrent();
            return new System.Security.Principal.WindowsPrincipal(id).IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
        }
        catch { return false; }
    }
}

public class Dns01ManualHandler : IChallengeHandler
{
    // For manual DNS - just logs TXT record to create
    public Task PrepareAsync(string domain, string token, string keyAuthz, IProgress<string>? log, CancellationToken ct)
    {
        // keyAuthz here is actually TXT value? In Certes Dns challenge, DnsTxt is the value
        log?.Report($"[DNS-01] MANUAL: Utwórz rekord TXT dla _acme-challenge.{domain} o wartości:");
        log?.Report($"         {keyAuthz}");
        log?.Report($"[DNS-01] Po utworzeniu rekordu naciśnij kontynuuj... (automatycznie czekamy 5s w trybie auto)");
        return Task.Delay(5000, ct);
    }

    public Task CleanupAsync(string domain, string token, IProgress<string>? log, CancellationToken ct)
    {
        log?.Report($"[DNS-01] Możesz usunąć rekord TXT _acme-challenge.{domain}");
        return Task.CompletedTask;
    }
}

/// <summary>
/// Wybor handlera dns-01: Cloudflare (auto) albo Manual.
/// </summary>
public static class ChallengeHandlerFactory
{
    public const string Manual = "Manual";
    public const string Cloudflare = "Cloudflare";
    public const string Route53 = "Route53";
    public const string CloudflareTokenKey = "CloudflareApiToken";
    public const string AwsAccessKeyId = "AwsAccessKeyId";
    public const string AwsSecretKey = "AwsSecretAccessKey";

    public static string DnsProvider(ManagedCertificate cert) =>
        cert.ChallengeConfig.DnsProvider;

    public static bool IsCloudflare(ManagedCertificate cert) =>
        cert.ChallengeConfig.DnsProvider.Equals(Cloudflare, StringComparison.OrdinalIgnoreCase)
        && !string.IsNullOrWhiteSpace(CloudflareToken(cert));

    public static string? CloudflareToken(ManagedCertificate cert) =>
        cert.ChallengeConfig.DnsProviderCredentials.TryGetValue(CloudflareTokenKey, out var t)
        && !string.IsNullOrWhiteSpace(t) ? t : null;

    public static bool IsRoute53(ManagedCertificate cert) =>
        cert.ChallengeConfig.DnsProvider.Equals(Route53, StringComparison.OrdinalIgnoreCase)
        && !string.IsNullOrWhiteSpace(AwsKey(cert)) && !string.IsNullOrWhiteSpace(AwsSecret(cert));

    public static string? AwsKey(ManagedCertificate cert) =>
        cert.ChallengeConfig.DnsProviderCredentials.TryGetValue(AwsAccessKeyId, out var t)
        && !string.IsNullOrWhiteSpace(t) ? t : null;

    public static string? AwsSecret(ManagedCertificate cert) =>
        cert.ChallengeConfig.DnsProviderCredentials.TryGetValue(AwsSecretKey, out var t)
        && !string.IsNullOrWhiteSpace(t) ? t : null;

    public static IChallengeHandler CreateDns(ManagedCertificate cert)
    {
        var cfToken = CloudflareToken(cert);
        if (cert.ChallengeConfig.DnsProvider.Equals(Cloudflare, StringComparison.OrdinalIgnoreCase) && cfToken != null)
            return new CloudflareDns01Handler(cfToken, cert.ChallengeConfig.DnsPropagationSeconds);
        var key = AwsKey(cert);
        var secret = AwsSecret(cert);
        if (cert.ChallengeConfig.DnsProvider.Equals(Route53, StringComparison.OrdinalIgnoreCase) && key != null && secret != null)
            return new Route53Dns01Handler(key, secret, cert.ChallengeConfig.DnsPropagationSeconds);
        return new Dns01ManualHandler();
    }

    /// <summary>Wartosc rekordu TXT dla dns-01 (RFC 8555 8.4): base64url(SHA256(keyAuthorization)).</summary>
    public static string DnsTxtValue(string keyAuthorization)
    {
        using var sha = System.Security.Cryptography.SHA256.Create();
        return AcmeJws.B64U(sha.ComputeHash(System.Text.Encoding.ASCII.GetBytes(keyAuthorization)));
    }
}
