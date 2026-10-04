using Certify.ACME;
using Certify.Core.Localization;
using Certify.Core.Models;
using Certify.Core.Services;
using Certify.Deployment;

namespace Certify.Service;

/// <summary>Sklada RenewalService bez UI (usluga Windows i tryb --renew).</summary>
public static class RenewalHost
{
    public static RenewalService Create(AppSettings settings)
    {
        // Jezyk e-maili i komunikatow statusu jak w GUI.
        UIStrings.Lang = UIStrings.ResolveLang(settings.Language,
            System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName);
        var store = new CertificateStore(settings);
        var acme = new LetsEncryptService(settings);
        var deployers = new List<IDeploymentTarget> { new IisDeployer(), new ApacheDeployer(), new NginxDeployer() };
        return new RenewalService(store, acme, deployers, settings, new EmailNotifier(settings));
    }
}

/// <summary>
/// Log do pliku logs/&lt;prefix&gt;-&lt;data&gt;.txt. Synchroniczny IProgress z blokada:
/// Progress&lt;T&gt; bez kontekstu synchronizacji wolalby callbacki rownolegle na puli
/// watkow (kolejnosc linii i kolizje zapisu do pliku).
/// </summary>
public sealed class FileLog : IProgress<string>
{
    private readonly string _dir;
    private readonly string _prefix;
    private readonly object _gate = new();

    public FileLog(string logsDirectory, string prefix)
    {
        _dir = logsDirectory;
        _prefix = prefix;
    }

    public void Report(string message)
    {
        lock (_gate)
        {
            try
            {
                Directory.CreateDirectory(_dir);
                File.AppendAllText(Path.Combine(_dir, $"{_prefix}-{DateTime.Now:yyyy-MM-dd}.txt"),
                    $"[{DateTime.Now:HH:mm:ss}] {message}\n");
            }
            catch { }
        }
    }
}
