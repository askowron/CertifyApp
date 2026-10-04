using System.Diagnostics;
using Certify.Core.Models;
using Certify.Core.Services;

namespace Certify.Deployment;

public class ApacheDeployer : IDeploymentTarget
{
    public DeploymentTargetType TargetType => DeploymentTargetType.Apache;

    public async Task<DeploymentResult> DeployAsync(ManagedCertificate cert, DeploymentTarget config, IProgress<string>? log, CancellationToken ct)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(cert.CertPath) || string.IsNullOrWhiteSpace(cert.KeyPath))
                return new DeploymentResult { Success = false, Message = "Brak plików cert/key" };

            var destDir = config.CertificateOutputPath;
            if (string.IsNullOrWhiteSpace(destDir))
            {
                // Bez ConfigPath "." = katalog roboczy procesu (dla --renew jako SYSTEM: System32).
                if (string.IsNullOrWhiteSpace(config.ConfigPath))
                    return new DeploymentResult { Success = false, Message = "Brak katalogu docelowego (CertificateOutputPath/ConfigPath)" };
                destDir = Path.Combine(Path.GetDirectoryName(config.ConfigPath)!, "ssl");
            }

            Directory.CreateDirectory(destDir);
            var baseName = CertificateArtifactWriter.SafeFileName(cert.PrimaryDomain);
            var destCrt = Path.Combine(destDir, $"{baseName}.crt");
            var destKey = Path.Combine(destDir, $"{baseName}.key");
            var destChain = Path.Combine(destDir, $"{baseName}-chain.crt");

            File.Copy(cert.CertPath!, destCrt, true);
            File.Copy(cert.KeyPath!, destKey, true);
            if (File.Exists(Path.Combine(Path.GetDirectoryName(cert.CertPath!)!, "chain.pem")))
                File.Copy(Path.Combine(Path.GetDirectoryName(cert.CertPath!)!, "chain.pem"), destChain, true);

            log?.Report($"[Apache] Skopiowano cert do {destDir}");

            // Patch vhost config if provided
            if (!string.IsNullOrWhiteSpace(config.ConfigPath) && File.Exists(config.ConfigPath))
            {
                var content = await File.ReadAllTextAsync(config.ConfigPath, ct);
                var original = content;
                content = PatchApacheConfig(content, destCrt, destKey, destChain);
                if (content != original)
                {
                    await File.WriteAllTextAsync(config.ConfigPath, content, ct);
                    log?.Report($"[Apache] Zaktualizowano {config.ConfigPath}");
                }
                else
                {
                    log?.Report($"[Apache] Config nie wymaga zmian lub nie znaleziono dyrektyw SSL");
                }
            }

            TryReloadService(config.ServiceName, log);

            return new DeploymentResult { Success = true, Message = $"Apache deployed to {destDir}" };
        }
        catch (Exception ex)
        {
            return new DeploymentResult { Success = false, Message = ex.Message };
        }
    }

    private static string PatchApacheConfig(string content, string crt, string key, string chain)
    {
        // Simple: replace or insert SSLCertificateFile / KeyFile
        // MatchEvaluator zamiast stringu zastepczego: '$' w sciezce bylby podstawieniem grupy.
        // [^\r\n]* zamiast .* - nie zjada \r w plikach CRLF.
        bool hasCrt = content.Contains("SSLCertificateFile");
        bool hasKey = content.Contains("SSLCertificateKeyFile");
        if (hasCrt) content = System.Text.RegularExpressions.Regex.Replace(content, @"\bSSLCertificateFile[ \t]+[^\r\n]*", _ => $"SSLCertificateFile {crt}");
        if (hasKey) content = System.Text.RegularExpressions.Regex.Replace(content, @"\bSSLCertificateKeyFile[ \t]+[^\r\n]*", _ => $"SSLCertificateKeyFile {key}");
        // Add chain if not present
        if (!content.Contains("SSLCertificateChainFile") && File.Exists(chain))
        {
            // insert after cert file line
            content = content.Replace($"SSLCertificateFile {crt}", $"SSLCertificateFile {crt}\n    SSLCertificateChainFile {chain}");
        }
        return content;
    }

    private static void TryReloadService(string? serviceName, IProgress<string>? log)
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                // Puste / "apache2" (stary domyslny) -> wykryj usluge Apache* (Apache Lounge: "Apache2.4").
                if (!WindowsServiceHelper.TryRestart(serviceName, "Apache", log, "Apache"))
                    log?.Report("[Apache] UWAGA: nie zrestartowano Apache (brak usługi Apache* lub błąd) - " +
                                "ustaw nazwę usługi w certyfikacie albo zrestartuj Apache ręcznie, inaczej zostanie stary certyfikat.");
            }
            else
            {
                var svc = string.IsNullOrWhiteSpace(serviceName) ? "apache2" : serviceName;
                var psi = new ProcessStartInfo("systemctl", $"reload {svc}") { RedirectStandardOutput = true, UseShellExecute = false };
                using var p = Process.Start(psi);
                p?.WaitForExit(5000);
                log?.Report($"[Apache] systemctl reload {svc} exit={p?.ExitCode}");
            }
        }
        catch (Exception ex) { log?.Report($"[Apache] Reload warn: {ex.Message}"); }
    }

    public Task<List<string>> DiscoverSitesAsync()
    {
        // Not trivial without parsing config, return empty
        return Task.FromResult(new List<string>());
    }
}
