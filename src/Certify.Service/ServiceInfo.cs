namespace Certify.Service;

/// <summary>Stale uslugi Windows (wspolne dla uslugi i instalatora w GUI).</summary>
public static class ServiceInfo
{
    public const string Name = "CertifyAppRenewal";
    public const string DisplayName = "CertifyApp Renewal";
    public const string Description = "Automatyczne odnawianie certyfikatow ACME oznaczonych w CertifyApp jako auto-odnawiane.";
    public const string ExeName = "Certify.Service.exe";

    /// <summary>
    /// Staly katalog uslugi. Katalog instalacji ClickOnce zmienia sie przy kazdej
    /// aktualizacji (stary jest usuwany), wiec binPath uslugi nie moze tam wskazywac.
    /// </summary>
    public static string InstallDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "CertifyApp", "Service");

    /// <summary>Pierwszy przebieg po starcie uslugi (system zdazy podniesc siec/IIS).</summary>
    public static readonly TimeSpan StartupDelay = TimeSpan.FromMinutes(2);

    /// <summary>Ponowna proba, gdy operacje trzyma GUI.</summary>
    public static readonly TimeSpan BusyRetry = TimeSpan.FromMinutes(5);

    public static readonly TimeSpan MinInterval = TimeSpan.FromMinutes(15);
}
