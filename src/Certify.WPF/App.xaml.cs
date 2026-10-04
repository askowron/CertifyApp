using System.Windows;
using Certify.Core.Models;
using Certify.Core.Services;
using Certify.Service;

namespace Certify.WPF;

public partial class App : Application
{
    // Okno glowne tworzone recznie (bez StartupUri): przy ponownym uruchomieniu z UAC
    // i w trybie --renew WPF nie moze juz samo otwierac MainWindow po OnStartup.
    protected override async void OnStartup(StartupEventArgs e)
    {
        // Jezyk przed utworzeniem okien.
        try
        {
            var langSettings = new SettingsStore(new AppSettings()).Load();
            WpfLocalizer.Apply(langSettings);
        }
        catch { WpfLocalizer.ApplyLang("pl"); }

        base.OnStartup(e);

        // Wymagane uprawnienia Administratora (IIS, LocalMachine\My, zapis w C:\inetpub).
        // Manifest requireAdministrator odpada - ClickOnce go odrzuca przy publikacji -
        // wiec bez elewacji uruchamiamy sie ponownie przez UAC.
        if (!AdminHelper.IsElevated())
        {
            if (!AdminHelper.TryRelaunchElevated(e.Args))
            {
                MessageBox.Show(WpfLocalizer.T("App_Msg_AdminRequired"), "CertifyApp",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                Shutdown(1);
                return;
            }
            Shutdown(0);
            return;
        }

        // Headless renew mode for Task Scheduler
        if (e.Args.Contains("--renew"))
        {
            var settings = new SettingsStore(new AppSettings()).Load();
            var renewal = RenewalHost.Create(settings);
            var log = new FileLog(settings.LogsDirectory, "renew");
            log.Report("=== CertifyApp --renew start ===");
            // Wspolna blokada z GUI i usluga Windows; czekamy do 30 min na koniec ich operacji.
            if (await renewal.OperationLock.WaitAsync((int)TimeSpan.FromMinutes(30).TotalMilliseconds))
            {
                try { await renewal.CheckAndRenewAllAsync(log); }
                catch (Exception ex) { log.Report($"[Renewal] błąd: {ex}"); }
                finally { renewal.OperationLock.Release(); }
            }
            else log.Report("Trwa inna operacja (GUI/usługa) - pomijam przebieg.");
            log.Report("=== --renew done, exit ===");
            Shutdown(0);
            return;
        }

        AppInfo.RegisterWindowIcon();
        var main = new MainWindow();
        MainWindow = main;
        main.Show();
    }
}
