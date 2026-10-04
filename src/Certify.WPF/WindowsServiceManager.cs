using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.ServiceProcess;
using System.Text;
using Certify.Service;

namespace Certify.WPF;

/// <summary>
/// Instalacja/aktualizacja/usuwanie uslugi Windows (Certify.Service.exe).
/// Pliki aplikacji kopiowane do ServiceInfo.InstallDirectory (staly katalog -
/// ClickOnce zmienia swoj przy kazdej aktualizacji), rejestracja przez sc.exe.
/// Wywolywac pod RenewalService.OperationLock: wtedy usluga nie jest w trakcie odnawiania.
/// </summary>
public static class WindowsServiceManager
{
    private static readonly TimeSpan StatusTimeout = TimeSpan.FromSeconds(60);

    private static string SourceDirectory => AppContext.BaseDirectory;

    /// <summary>
    /// Pliki aplikacji bez katalogu uslugi. Instalator NSIS kladzie aplikacje do %ProgramFiles%\CertifyApp,
    /// wiec katalog uslugi (CertifyApp\Service) jest jej podkatalogiem - bez tego kopiowalby sam siebie.
    /// </summary>
    private static IEnumerable<string> SourceFiles()
    {
        var svcDir = Path.TrimEndingDirectorySeparator(Path.GetFullPath(ServiceInfo.InstallDirectory)) + Path.DirectorySeparatorChar;
        return Directory.EnumerateFiles(SourceDirectory, "*", SearchOption.AllDirectories)
            .Where(f => !f.StartsWith(svcDir, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Aplikacja uruchomiona z katalogu uslugi - nie kopiowac ani nie usuwac go.</summary>
    private static bool RunsFromServiceDirectory => string.Equals(
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(SourceDirectory)),
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(ServiceInfo.InstallDirectory)),
        StringComparison.OrdinalIgnoreCase);

    /// <summary>null = usluga nie jest zainstalowana.</summary>
    public static ServiceControllerStatus? GetStatus()
    {
        try
        {
            using var sc = new ServiceController(ServiceInfo.Name);
            return sc.Status;
        }
        catch (InvalidOperationException) { return null; }
    }

    public static bool IsRunning => GetStatus() == ServiceControllerStatus.Running;

    /// <summary>Pliki w katalogu uslugi rozne od plikow biezacej aplikacji (rozmiar/data zapisu).</summary>
    public static bool NeedsUpdate()
    {
        if (RunsFromServiceDirectory) return false;
        if (!Directory.Exists(ServiceInfo.InstallDirectory)) return true;
        foreach (var src in SourceFiles())
        {
            var dst = new FileInfo(Path.Combine(ServiceInfo.InstallDirectory, Path.GetRelativePath(SourceDirectory, src)));
            var s = new FileInfo(src);
            if (!dst.Exists || dst.Length != s.Length || dst.LastWriteTimeUtc != s.LastWriteTimeUtc) return true;
        }
        return false;
    }

    public static void Install(IProgress<string> log)
    {
        if (GetStatus() != null) { log.Report("[Service] Usługa już zainstalowana - aktualizuję pliki."); Update(log); return; }
        CopyFiles(log);
        var exe = Path.Combine(ServiceInfo.InstallDirectory, ServiceInfo.ExeName);
        // ArgumentList cytuje wartosc z cudzyslowami: binPath= "\"C:\Program Files\...\Certify.Service.exe\""
        RunSc(log, "create", ServiceInfo.Name, "binPath=", $"\"{exe}\"", "start=", "delayed-auto", "DisplayName=", ServiceInfo.DisplayName);
        RunSc(log, "description", ServiceInfo.Name, ServiceInfo.Description);
        // Restart po awarii: 1 min, 1 min, 5 min; licznik zerowany po dobie.
        RunSc(log, "failure", ServiceInfo.Name, "reset=", "86400", "actions=", "restart/60000/restart/60000/restart/300000");
        Start(log);
    }

    public static void Update(IProgress<string> log)
    {
        var wasRunning = IsRunning;
        Stop(log);
        CopyFiles(log);
        if (wasRunning) Start(log);
    }

    public static void Uninstall(IProgress<string> log)
    {
        if (GetStatus() == null) { log.Report("[Service] Usługa nie jest zainstalowana."); return; }
        Stop(log);
        RunSc(log, "delete", ServiceInfo.Name);
        // SCM usuwa wpis dopiero po zamknieciu wszystkich uchwytow - poczekaj chwile.
        var sw = Stopwatch.StartNew();
        while (GetStatus() != null && sw.Elapsed < StatusTimeout) Thread.Sleep(500);
        if (RunsFromServiceDirectory) return;
        for (var i = 0; i < 10; i++)
        {
            try
            {
                if (Directory.Exists(ServiceInfo.InstallDirectory)) Directory.Delete(ServiceInfo.InstallDirectory, recursive: true);
                log.Report($"[Service] Usunięto katalog {ServiceInfo.InstallDirectory}");
                break;
            }
            catch (Exception ex) when (i < 9 && ex is IOException or UnauthorizedAccessException) { Thread.Sleep(1000); }
            catch (Exception ex) { log.Report($"[Service] Nie usunięto katalogu {ServiceInfo.InstallDirectory}: {ex.Message}"); }
        }
    }

    public static void Start(IProgress<string> log)
    {
        using var sc = new ServiceController(ServiceInfo.Name);
        if (sc.Status == ServiceControllerStatus.Running) return;
        log.Report($"[Service] Uruchamiam {ServiceInfo.Name}...");
        sc.Start();
        sc.WaitForStatus(ServiceControllerStatus.Running, StatusTimeout);
        log.Report("[Service] Usługa działa.");
    }

    public static void Stop(IProgress<string> log)
    {
        if (GetStatus() is null or ServiceControllerStatus.Stopped) return;
        using var sc = new ServiceController(ServiceInfo.Name);
        log.Report($"[Service] Zatrzymuję {ServiceInfo.Name}...");
        if (sc.CanStop) sc.Stop();
        sc.WaitForStatus(ServiceControllerStatus.Stopped, StatusTimeout);
        log.Report("[Service] Usługa zatrzymana.");
    }

    private static void CopyFiles(IProgress<string> log)
    {
        if (RunsFromServiceDirectory) return;
        var count = 0;
        foreach (var src in SourceFiles())
        {
            var dst = Path.Combine(ServiceInfo.InstallDirectory, Path.GetRelativePath(SourceDirectory, src));
            Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
            File.Copy(src, dst, overwrite: true); // zachowuje date zapisu - NeedsUpdate porownuje po niej
            count++;
        }
        log.Report($"[Service] Skopiowano {count} plików do {ServiceInfo.InstallDirectory}");
    }

    private static Encoding OemEncoding
    {
        get
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            try { return Encoding.GetEncoding(CultureInfo.CurrentCulture.TextInfo.OEMCodePage); }
            catch { return Encoding.Default; }
        }
    }

    private static void RunSc(IProgress<string> log, params string[] args)
    {
        var psi = new ProcessStartInfo("sc.exe")
        {
            UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true,
            // sc.exe pisze w stronie kodowej OEM (852 dla PL) - bez tego polskie znaki w logu sa krzakami.
            StandardOutputEncoding = OemEncoding, StandardErrorEncoding = OemEncoding
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi) ?? throw new InvalidOperationException("Nie uruchomiono sc.exe");
        var errTask = p.StandardError.ReadToEndAsync();
        var outp = p.StandardOutput.ReadToEnd().Trim();
        var errp = errTask.Result.Trim();
        p.WaitForExit();
        if (outp.Length > 0) log.Report(outp);
        if (errp.Length > 0) log.Report(errp);
        if (p.ExitCode != 0)
            throw new InvalidOperationException($"sc {args[0]} zakończone kodem {p.ExitCode}");
    }
}
