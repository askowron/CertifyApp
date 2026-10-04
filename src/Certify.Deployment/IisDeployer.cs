using System.Diagnostics;
using System.Security.Cryptography.X509Certificates;
using Certify.Core.Models;
using Certify.Core.Services;
using Microsoft.Web.Administration;

namespace Certify.Deployment;

public class IisDeployer : IDeploymentTarget
{
    public DeploymentTargetType TargetType => DeploymentTargetType.IIS;

    public async Task<DeploymentResult> DeployAsync(ManagedCertificate cert, DeploymentTarget config, IProgress<string>? log, CancellationToken ct)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(cert.PfxPath) || !File.Exists(cert.PfxPath))
                return new DeploymentResult { Success = false, Message = "Brak pliku PFX" };

            var siteName = config.SiteId;
            if (string.IsNullOrWhiteSpace(siteName))
                return new DeploymentResult { Success = false, Message = "Brak nazwy SiteId dla IIS" };

            log?.Report($"[IIS] Instaluję certyfikat do magazynu LocalMachine\\My ...");
            var pfxBytes = await File.ReadAllBytesAsync(cert.PfxPath!, ct);
            var password = cert.PfxPassword ?? "certify";
            var pfxCollection = new X509Certificate2Collection();
            pfxCollection.Import(pfxBytes, password, X509KeyStorageFlags.PersistKeySet | X509KeyStorageFlags.MachineKeySet);
            var x509 = pfxCollection.Cast<X509Certificate2>().FirstOrDefault(c => c.HasPrivateKey)
                ?? throw new InvalidOperationException("PFX bez certyfikatu z kluczem prywatnym");

            using (var store = new X509Store(StoreName.My, StoreLocation.LocalMachine))
            {
                store.Open(OpenFlags.ReadWrite);
                var existing = store.Certificates.Find(X509FindType.FindByThumbprint, x509.Thumbprint, false);
                if (existing.Count == 0)
                {
                    store.Add(x509);
                    log?.Report($"[IIS] Dodano cert {x509.Thumbprint} do store");
                }
                else
                {
                    log?.Report($"[IIS] Cert {x509.Thumbprint} już w store");
                }
            }

            // Intermediates do LocalMachine\CA - inaczej IIS wysyla niepelny lancuch
            // (klienci bez AIA fetch, np. curl/Java/Android, odrzucaja polaczenie).
            using (var caStore = new X509Store(StoreName.CertificateAuthority, StoreLocation.LocalMachine))
            {
                caStore.Open(OpenFlags.ReadWrite);
                foreach (var ic in pfxCollection.Cast<X509Certificate2>().Where(c => !c.HasPrivateKey && c.Subject != c.Issuer))
                {
                    if (caStore.Certificates.Find(X509FindType.FindByThumbprint, ic.Thumbprint, false).Count == 0)
                    {
                        caStore.Add(ic);
                        log?.Report($"[IIS] Dodano intermediate {ic.Subject} do LocalMachine\\CA");
                    }
                }
            }

            log?.Report($"[IIS] Konfiguruję binding dla site '{siteName}' ...");
            try
            {
                using var mgr = new ServerManager();
                var site = mgr.Sites.FirstOrDefault(s => s.Name.Equals(siteName, StringComparison.OrdinalIgnoreCase));
                if (site == null)
                    return new DeploymentResult { Success = false, Message = $"Site '{siteName}' nie znaleziony w IIS" };

                var hash = x509.GetCertHash();
                foreach (var domain in cert.Domains)
                {
                    var (bindingInfo, useSni) = BuildHttpsBindingInfo(domain);
                    var existingBinding = site.Bindings.FirstOrDefault(b =>
                        b.Protocol == "https" && b.BindingInformation.Equals(bindingInfo, StringComparison.OrdinalIgnoreCase));
                    // Fallback tylko na binding https bez hosta (*:443:) - nigdy na binding
                    // innej domeny (wczesniej podmieniany byl cert pierwszego z brzegu https).
                    existingBinding ??= site.Bindings.FirstOrDefault(b =>
                        b.Protocol == "https" && b.BindingInformation.EndsWith(":443:", StringComparison.Ordinal));
                    if (existingBinding == null)
                    {
                        var added = site.Bindings.Add(bindingInfo, hash, "My");
                        // sslFlags=1 (SNI); MWA 7.0.0 nie ma wlasciwosci SslFlags - atrybut konfiguracji.
                        if (useSni) added.SetAttributeValue("sslFlags", 1);
                        log?.Report($"[IIS] Dodano binding {bindingInfo}{(useSni ? " (SNI)" : "")}");
                    }
                    else
                    {
                        existingBinding.CertificateHash = hash;
                        existingBinding.CertificateStoreName = "My";
                        log?.Report($"[IIS] Zaktualizowano binding {existingBinding.BindingInformation}");
                    }
                }
                mgr.CommitChanges();
                log?.Report("[IIS] CommitChanges OK");
            }
            catch (Exception ex) when (ex.Message.Contains("Redirection.config") || ex is UnauthorizedAccessException)
            {
                // Bez uprawnien do konfiguracji IIS bindingi NIE zostaly zmienione -
                // podaj komende do recznego wykonania i zglos blad (wczesniej: falszywy sukces).
                var args = $"http add sslcert ipport=0.0.0.0:443 certhash={x509.Thumbprint} appid={{4dc3e181-e14b-4a21-b022-59fc669b0914}} certstorename=MY";
                log?.Report($"[IIS] ServerManager błąd: {ex.Message}");
                log?.Report($"[IIS] Wykonaj ręcznie jako Administrator: netsh {args}");
                return new DeploymentResult { Success = false, Message = $"Brak uprawnień do konfiguracji IIS (uruchom jako Administrator): {ex.Message}" };
            }

            return new DeploymentResult { Success = true, Message = $"IIS site '{siteName}' zaktualizowany" };
        }
        catch (Exception ex)
        {
            return new DeploymentResult { Success = false, Message = ex.Message };
        }
    }

    /// <summary>
    /// Binding https dla identyfikatora: domena -> "*:443:host" z SNI,
    /// adres IP -> "ip:443:" (bez hosta, IPv6 w nawiasach, bez SNI).
    /// </summary>
    public static (string BindingInfo, bool UseSni) BuildHttpsBindingInfo(string domain)
    {
        var d = domain.Trim();
        if (System.Net.IPAddress.TryParse(d.Trim('[', ']'), out var ip))
        {
            var ipPart = ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 ? $"[{ip}]" : ip.ToString();
            return ($"{ipPart}:443:", false);
        }
        return ($"*:443:{d}", true);
    }

    public Task<List<string>> DiscoverSitesAsync() =>
        DiscoverSitesDetailedAsync().ContinueWith(t => t.Result.Sites);

    /// <summary>
    /// Lista site'ow + powod bledu (nigdy null). Najpierw ServerManager,
    /// potem fallback appcmd (pomaga gdy pakiet MWA nie dziala, a config czytelny).
    /// Pusta lista + powod = pokazac w UI (wczesniej blad byl polykaney po cichu).
    /// </summary>
    public async Task<(List<string> Sites, string? Error)> DiscoverSitesDetailedAsync()
    {
        try
        {
            using var mgr = new ServerManager();
            var names = mgr.Sites.Select(s => s.Name).ToList();
            if (names.Count > 0) return (names, null);
            // Zero site'ow to tez podejrzane - sprobuj appcmd zanim uwierzymy.
            var viaAppCmd = await TryAppCmdSitesAsync();
            return viaAppCmd.Count > 0 ? (viaAppCmd, null) : (names, null);
        }
        catch (Exception ex)
        {
            var viaAppCmd = await TryAppCmdSitesAsync();
            if (viaAppCmd.Count > 0) return (viaAppCmd, null);
            var reason = ex is UnauthorizedAccessException || IsAccessDenied(ex)
                ? "brak uprawnien (uruchom jako Administrator)"
                : Short(ex.Message);
            return (new List<string>(), reason);
        }
    }

    private static bool IsAccessDenied(Exception ex)
    {
        var m = ex.Message;
        return m.Contains("insufficient permissions", StringComparison.OrdinalIgnoreCase)
            || m.Contains("redirection.config", StringComparison.OrdinalIgnoreCase)
            || m.Contains("0x80070005", StringComparison.OrdinalIgnoreCase)
            || ex.InnerException is UnauthorizedAccessException;
    }

    private static string Short(string s) => s.Length > 120 ? s[..120] + "..." : s;

    private static async Task<List<string>> TryAppCmdSitesAsync()
    {
        try
        {
            var windir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            var appcmd = Path.Combine(windir, "system32", "inetsrv", "appcmd.exe");
            if (!File.Exists(appcmd))
            {
                var alt = Path.Combine(windir, "Sysnative", "inetsrv", "appcmd.exe");
                if (File.Exists(alt)) appcmd = alt;
                else return new List<string>();
            }
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var psi = new ProcessStartInfo(appcmd, "list site /text:name")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var proc = Process.Start(psi);
            if (proc == null) return new List<string>();
            var stdout = await proc.StandardOutput.ReadToEndAsync(cts.Token);
            await proc.WaitForExitAsync(cts.Token);
            return proc.ExitCode == 0 ? IisSiteDiscovery.ParseAppCmdOutput(stdout) : new List<string>();
        }
        catch
        {
            return new List<string>();
        }
    }

    /// <summary>
    /// Sciezka fizyczna katalogu glownego site'u (aplikacja "/", vdir "/") z rozwinietymi
    /// zmiennymi (%SystemDrive%\inetpub\wwwroot). Null = brak site'u/uprawnien.
    /// </summary>
    public string? GetSitePhysicalPath(string siteName)
    {
        try
        {
            using var mgr = new ServerManager();
            var site = mgr.Sites.FirstOrDefault(s => s.Name.Equals(siteName, StringComparison.OrdinalIgnoreCase));
            var path = site?.Applications["/"]?.VirtualDirectories["/"]?.PhysicalPath;
            return string.IsNullOrWhiteSpace(path) ? null : Environment.ExpandEnvironmentVariables(path);
        }
        catch
        {
            return null;
        }
    }

    public Task<List<string>> DiscoverSiteBindingsAsync(string siteName)
    {
        try
        {
            using var mgr = new ServerManager();
            var site = mgr.Sites.FirstOrDefault(s => s.Name.Equals(siteName, StringComparison.OrdinalIgnoreCase));
            if (site == null) return Task.FromResult(new List<string>());
            var hosts = IisBindingHelper.ExtractHosts(
                site.Bindings.Select(b => (b.Protocol, b.BindingInformation)));
            return Task.FromResult(hosts);
        }
        catch
        {
            return Task.FromResult(new List<string>());
        }
    }
}

/// <summary>
/// Wyciaga hostnames z IIS BindingInformation ("IP:port:host").
/// Czysta funkcja bez ServerManagera - testowalna.
/// </summary>
public static class IisBindingHelper
{
    public static List<string> ExtractHosts(IEnumerable<(string Protocol, string BindingInformation)> bindings)
    {
        var hosts = new List<string>();
        foreach (var (protocol, info) in bindings)
        {
            if (!protocol.Equals("http", StringComparison.OrdinalIgnoreCase) &&
                !protocol.Equals("https", StringComparison.OrdinalIgnoreCase))
                continue;
            if (string.IsNullOrWhiteSpace(info)) continue;
            // host jest po ostatnim ':' (odporne na IPv6 w czesci IP)
            var last = info.LastIndexOf(':');
            if (last < 0 || last == info.Length - 1) continue;
            var host = info[(last + 1)..].Trim().TrimEnd('.').ToLowerInvariant();
            if (string.IsNullOrWhiteSpace(host) || host == "*") continue;
            if (!hosts.Contains(host)) hosts.Add(host);
        }
        return hosts;
    }
}

/// <summary>
/// Parsuje `appcmd list site /text:name` (jedna nazwa na linie, czasem w cudzyslowach).
/// Czysta funkcja - testowalna.
/// </summary>
public static class IisSiteDiscovery
{
    public static List<string> ParseAppCmdOutput(string stdout)
    {
        var sites = new List<string>();
        foreach (var raw in stdout.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var name = raw.Trim().Trim('"').Trim();
            if (name.Length == 0) continue;
            if (name.StartsWith("SITE ", StringComparison.OrdinalIgnoreCase)) continue; // naglowki
            if (!sites.Contains(name, StringComparer.OrdinalIgnoreCase)) sites.Add(name);
        }
        return sites;
    }
}
