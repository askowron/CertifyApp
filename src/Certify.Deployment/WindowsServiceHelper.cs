using System.Diagnostics;

namespace Certify.Deployment;

/// <summary>
/// Wykrywanie i restart uslug Windows dla Apache/Nginx (sc.exe / net.exe - bez System.ServiceProcess,
/// projekt jest net8.0). Apache Lounge rejestruje usluge jako "Apache2.4", nie "apache2" jak na Linuksie.
/// </summary>
public static class WindowsServiceHelper
{
    private static readonly TimeSpan NetTimeout = TimeSpan.FromSeconds(60);

    /// <summary>Nazwy uslug z wyjscia "sc query" (linie "SERVICE_NAME: x" - pola nie sa lokalizowane).</summary>
    public static List<string> ParseServiceNames(string scQueryOutput) =>
        scQueryOutput.Split('\n')
            .Select(l => l.Trim())
            .Where(l => l.StartsWith("SERVICE_NAME:", StringComparison.OrdinalIgnoreCase))
            .Select(l => l["SERVICE_NAME:".Length..].Trim())
            .Where(n => n.Length > 0)
            .ToList();

    /// <summary>
    /// Skonfigurowana usluga, jesli istnieje; inaczej pierwsza zaczynajaca sie od prefiksu
    /// (kolejnosc prefiksow = priorytet). Null = nic nie pasuje.
    /// </summary>
    public static string? Pick(IReadOnlyCollection<string> installed, string? configured, params string[] prefixes)
    {
        if (!string.IsNullOrWhiteSpace(configured))
        {
            var exact = installed.FirstOrDefault(n => n.Equals(configured.Trim(), StringComparison.OrdinalIgnoreCase));
            if (exact != null) return exact;
        }
        foreach (var prefix in prefixes)
        {
            var match = installed.Where(n => n.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase).FirstOrDefault();
            if (match != null) return match;
        }
        return null;
    }

    public static List<string> InstalledServices()
    {
        try
        {
            var psi = new ProcessStartInfo("sc.exe") { UseShellExecute = false, RedirectStandardOutput = true, CreateNoWindow = true };
            foreach (var a in new[] { "query", "type=", "service", "state=", "all" }) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi);
            if (p == null) return new List<string>();
            var output = p.StandardOutput.ReadToEnd();
            p.WaitForExit();
            return ParseServiceNames(output);
        }
        catch { return new List<string>(); }
    }

    /// <summary>
    /// Wybiera usluge (Pick) i restartuje ja. False = nie znaleziono uslugi albo restart nieudany;
    /// powod jest w logu. area = prefiks logu, np. "Apache".
    /// </summary>
    public static bool TryRestart(string? configured, string area, IProgress<string>? log, params string[] prefixes)
    {
        var svc = Pick(InstalledServices(), configured, prefixes);
        if (svc == null) return false;
        if (!string.IsNullOrWhiteSpace(configured) && !svc.Equals(configured.Trim(), StringComparison.OrdinalIgnoreCase))
            log?.Report($"[{area}] Brak usługi '{configured}' - używam wykrytej '{svc}'.");
        // net stop zwraca 2, gdy usluga juz stoi - wtedy i tak startujemy.
        var stop = RunNet("stop", svc);
        var start = RunNet("start", svc);
        log?.Report($"[{area}] Restart usługi {svc}: stop exit={stop}, start exit={start}");
        return start == 0;
    }

    private static int RunNet(string verb, string service)
    {
        var psi = new ProcessStartInfo("net") { UseShellExecute = false, CreateNoWindow = true };
        psi.ArgumentList.Add(verb);
        psi.ArgumentList.Add(service);
        using var p = Process.Start(psi);
        if (p == null) return -1;
        return p.WaitForExit(NetTimeout) ? p.ExitCode : -1;
    }
}
