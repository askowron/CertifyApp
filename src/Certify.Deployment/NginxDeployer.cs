using System.Diagnostics;
using Certify.Core.Models;
using Certify.Core.Services;

namespace Certify.Deployment;

public class NginxDeployer : IDeploymentTarget
{
    public DeploymentTargetType TargetType => DeploymentTargetType.Nginx;

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
            // Nginx wants fullchain
            var destFullchain = Path.Combine(destDir, $"{baseName}-fullchain.crt");

            File.Copy(cert.CertPath!, destCrt, true);
            File.Copy(cert.KeyPath!, destKey, true);
            var chainPath = Path.Combine(Path.GetDirectoryName(cert.CertPath!)!, "chain.pem");
            if (File.Exists(chainPath))
                File.Copy(chainPath, destFullchain, true);
            else
                File.Copy(cert.CertPath!, destFullchain, true);

            log?.Report($"[Nginx] Skopiowano cert do {destDir}");

            if (!string.IsNullOrWhiteSpace(config.ConfigPath) && File.Exists(config.ConfigPath))
            {
                var content = await File.ReadAllTextAsync(config.ConfigPath, ct);
                var original = content;
                // Nginx directives: ssl_certificate and ssl_certificate_key
                // (?<![\w]) - nie ruszaj proxy_ssl_certificate itp.; MatchEvaluator - '$' w sciezce.
                if (content.Contains("ssl_certificate"))
                    content = System.Text.RegularExpressions.Regex.Replace(content, @"(?<![\w])ssl_certificate\s+[^;]+;", _ => $"ssl_certificate {destFullchain};");
                if (content.Contains("ssl_certificate_key"))
                    content = System.Text.RegularExpressions.Regex.Replace(content, @"(?<![\w])ssl_certificate_key\s+[^;]+;", _ => $"ssl_certificate_key {destKey};");

                if (content != original)
                {
                    await File.WriteAllTextAsync(config.ConfigPath, content, ct);
                    log?.Report($"[Nginx] Zaktualizowano {config.ConfigPath}");
                }
            }

            // Test config and reload
            TryReloadNginx(config, log);

            return new DeploymentResult { Success = true, Message = $"Nginx deployed to {destDir}" };
        }
        catch (Exception ex)
        {
            return new DeploymentResult { Success = false, Message = ex.Message };
        }
    }

    /// <summary>
    /// nginx.exe dla configu: katalog configu albo jego rodzice (typowo C:\nginx\conf\nginx.conf -> C:\nginx\nginx.exe).
    /// Null = nie znaleziono (wtedy "nginx" z PATH).
    /// </summary>
    public static string? FindNginxExe(string? configPath)
    {
        if (string.IsNullOrWhiteSpace(configPath)) return null;
        var dir = Path.GetDirectoryName(Path.GetFullPath(configPath));
        for (var i = 0; i < 3 && dir != null; i++, dir = Path.GetDirectoryName(dir))
        {
            var exe = Path.Combine(dir, "nginx.exe");
            if (File.Exists(exe)) return exe;
        }
        return null;
    }

    private static void TryReloadNginx(DeploymentTarget config, IProgress<string>? log)
    {
        var serviceName = config.ServiceName;
        try
        {
            if (OperatingSystem.IsWindows())
            {
                // nginx jako usluga (NSSM/WinSW) -> restart uslugi; inaczej nginx -s reload.
                if (WindowsServiceHelper.TryRestart(serviceName, "Nginx", log, "nginx")) return;
                // -s reload czyta logs\nginx.pid wzgledem katalogu nginx - stad WorkingDirectory.
                var exe = FindNginxExe(config.ConfigPath);
                var psi = new ProcessStartInfo(exe ?? "nginx", "-s reload") { UseShellExecute = false, CreateNoWindow = true };
                if (exe != null) psi.WorkingDirectory = Path.GetDirectoryName(exe)!;
                using var p = Process.Start(psi);
                p?.WaitForExit(15000);
                log?.Report($"[Nginx] {exe ?? "nginx"} -s reload exit={p?.ExitCode}");
                if (p?.ExitCode != 0)
                    log?.Report("[Nginx] UWAGA: reload nieudany - nginx może dalej używać starego certyfikatu; zrestartuj go ręcznie.");
            }
            else
            {
                var svc = string.IsNullOrWhiteSpace(serviceName) ? "nginx" : serviceName;
                var psi = new ProcessStartInfo("nginx", "-t") { RedirectStandardOutput = true, UseShellExecute = false };
                using var p = Process.Start(psi);
                p?.WaitForExit(5000);
                psi = new ProcessStartInfo("systemctl", $"reload {svc}") { RedirectStandardOutput = true, UseShellExecute = false };
                using var p2 = Process.Start(psi);
                p2?.WaitForExit(5000);
                log?.Report($"[Nginx] systemctl reload {svc} exit={p2?.ExitCode}");
            }
        }
        catch (Exception ex) { log?.Report($"[Nginx] Reload warn: {ex.Message}"); }
    }

    public Task<List<string>> DiscoverSitesAsync() => Task.FromResult(new List<string>());
}
