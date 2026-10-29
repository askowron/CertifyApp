using System.Diagnostics;

namespace Certify.Deployment;

/// <summary>
/// Wpis SNI (hostnameport=host:443) w http.sys przez netsh - dla bindingu IIS z hostem, ktorego certyfikat
/// zmieniamy (MWA 7.0.0 zapisuje zmiane CertificateHash do 0.0.0.0:443 zamiast do host:443).
/// </summary>
public static class HttpSysSni
{
    // Identyfikator aplikacji IIS w http.sys
    public const string IisAppId = "{4dc3e181-e14b-4a21-b022-59fc669b0914}";

    /// <summary>Dodaje albo aktualizuje wpis host:443; zwraca "dodano" / "zaktualizowano", blad netsh = wyjatek.</summary>
    public static string Upsert(string host, string thumbprint, Func<string, (int Exit, string Output)> run)
    {
        var hostPort = $"{host}:443";
        var (showExit, _) = run($"http show sslcert hostnameport={hostPort}");
        var verb = showExit == 0 ? "update" : "add";
        var (exit, output) = run($"http {verb} sslcert hostnameport={hostPort} certhash={thumbprint} appid={IisAppId} certstorename=MY");
        if (exit != 0)
            throw new InvalidOperationException($"netsh http {verb} sslcert {hostPort}: kod {exit}: {output.Trim()}");
        return verb == "add" ? "dodano" : "zaktualizowano";
    }

    public static (int Exit, string Output) RunNetsh(string args)
    {
        var psi = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "netsh.exe"), args)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        using var p = Process.Start(psi) ?? throw new InvalidOperationException("Nie uruchomiono netsh");
        var stdout = p.StandardOutput.ReadToEndAsync();
        var stderr = p.StandardError.ReadToEnd();
        p.WaitForExit();
        return (p.ExitCode, stdout.Result + stderr);
    }
}
