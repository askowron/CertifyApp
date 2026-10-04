using Certify.Core.Models;

namespace Certify.Core.Services;

public class BackgroundRenewalService : IDisposable
{
    private readonly RenewalService _renewal;
    private readonly AppSettings _settings;
    private Timer? _timer;
    private readonly IProgress<string>? _log;
    private readonly Action? _completed;

    /// <param name="completed">Po kazdym przebiegu (np. odswiezenie listy w UI,
    /// zeby UI nie nadpisalo odnowionego certu nieaktualna kopia).</param>
    public BackgroundRenewalService(RenewalService renewal, AppSettings settings, IProgress<string>? log = null, Action? completed = null)
    {
        _renewal = renewal;
        _settings = settings;
        _log = log;
        _completed = completed;
    }

    public bool IsRunning => _timer != null;

    public void Start()
    {
        if (!_settings.EnableBackgroundRenewal || _timer != null) return;
        _log?.Report($"[Background] Start auto-renew co {_settings.RenewalCheckInterval}");
        _timer = new Timer(async _ => await TickAsync(), null, TimeSpan.FromMinutes(2), _settings.RenewalCheckInterval);
    }

    /// <summary>Np. gdy odnawianie przejela usluga Windows.</summary>
    public void Stop()
    {
        if (_timer == null) return;
        _timer.Dispose();
        _timer = null;
        _log?.Report("[Background] Stop auto-renew w GUI");
    }

    private async Task TickAsync()
    {
        // Trwa operacja w UI (albo poprzedni przebieg) - pomin, sprobujemy przy kolejnym ticku.
        if (!await _renewal.OperationLock.WaitAsync(0))
        {
            _log?.Report("[Background] Trwa inna operacja - pomijam ten przebieg.");
            return;
        }
        try
        {
            await _renewal.CheckAndRenewAllAsync(_log);
        }
        catch (Exception ex) { _log?.Report($"[Background] błąd: {ex.Message}"); }
        finally
        {
            _renewal.OperationLock.Release();
        }
        try { _completed?.Invoke(); } catch { }
    }

    public void Dispose() => _timer?.Dispose();
}
