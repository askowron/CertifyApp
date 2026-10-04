using Certify.Core.Models;
using Certify.Core.Services;
using Microsoft.Extensions.Hosting;

namespace Certify.Service;

/// <summary>
/// Petla uslugi: co RenewalCheckInterval odnawia certyfikaty z RenewalMode.Auto
/// (RenewalService.CheckAndRenewAllAsync). Log w logs/service-&lt;data&gt;.txt.
/// </summary>
public class RenewalWorker : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var log = new FileLog(new AppSettings().LogsDirectory, "service");
        log.Report($"[Service] Start uslugi {ServiceInfo.Name} v{typeof(RenewalWorker).Assembly.GetName().Version}");
        try
        {
            await Task.Delay(ServiceInfo.StartupDelay, stoppingToken);
            while (!stoppingToken.IsCancellationRequested)
            {
                var next = await RunOnceAsync(log, stoppingToken);
                log.Report($"[Service] Następne sprawdzenie: {DateTime.Now + next:yyyy-MM-dd HH:mm}");
                await Task.Delay(next, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        log.Report("[Service] Stop uslugi");
    }

    /// <summary>Jeden przebieg; zwraca czas do nastepnego.</summary>
    public static async Task<TimeSpan> RunOnceAsync(IProgress<string> log, CancellationToken ct)
    {
        // Ustawienia i serwisy od nowa przy kazdym przebiegu: zmiany z GUI
        // (interwal, SMTP, domyslne CA) dzialaja bez restartu uslugi.
        AppSettings settings;
        try { settings = new SettingsStore(new AppSettings()).Load(); }
        catch (Exception ex)
        {
            log.Report($"[Service] Błąd odczytu ustawień: {ex.Message}");
            return ServiceInfo.BusyRetry;
        }
        var interval = settings.RenewalCheckInterval < ServiceInfo.MinInterval ? ServiceInfo.MinInterval : settings.RenewalCheckInterval;
        var renewal = RenewalHost.Create(settings);

        if (!await renewal.OperationLock.WaitAsync(0, ct))
        {
            log.Report("[Service] Trwa inna operacja (GUI) - ponowię za chwilę.");
            return ServiceInfo.BusyRetry;
        }
        try
        {
            log.Report("[Service] Sprawdzam certyfikaty do odnowienia");
            await renewal.CheckAndRenewAllAsync(log, ct);
            log.Report("[Service] Przebieg zakończony");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) { log.Report($"[Service] błąd: {ex}"); }
        finally
        {
            renewal.OperationLock.Release();
        }
        return interval;
    }
}
