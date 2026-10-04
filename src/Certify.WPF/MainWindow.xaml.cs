using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Input;
using Certify.ACME;
using Certify.Core.Models;
using Certify.Core.Services;
using Certify.Deployment;

namespace Certify.WPF;

public partial class MainWindow : Window
{
    private readonly AppSettings _settings;
    private readonly SettingsStore _settingsStore;
    private readonly EmailNotifier _email;
    private readonly CertificateStore _store;
    private readonly LetsEncryptService _acme;
    private readonly RenewalService _renewal;
    private readonly List<IDeploymentTarget> _deployers;
    private readonly BackgroundRenewalService _bg;

    private ObservableCollection<ManagedCertificateViewModel> _viewModels = new();

    // In Progress: biezaca operacja (cancel + elapsed).
    private CancellationTokenSource? _currentCts;
    private string _opName = "";
    private DateTime _opStart;
    private readonly System.Windows.Threading.DispatcherTimer _opTimer;

    public MainWindow()
    {
        InitializeComponent();
        UpdateTitle();
        WpfLocalizer.LanguageChanged += UpdateTitle;
        StatusText.Text = WpfLocalizer.T("Main_Status_Ready");
        StatusBarText.Text = WpfLocalizer.T("Main_Status_Ready");
        _settingsStore = new SettingsStore(new AppSettings());
        _settings = _settingsStore.Load();
        _email = new EmailNotifier(_settings);
        _store = new CertificateStore(_settings);
        _acme = new LetsEncryptService(_settings);
        _deployers = new List<IDeploymentTarget> { new IisDeployer(), new ApacheDeployer(), new NginxDeployer() };
        _renewal = new RenewalService(_store, _acme, _deployers, _settings, _email);

        CertsGrid.ItemsSource = _viewModels;
        Loaded += async (_, _) =>
        {
            await LoadCertsAsync();
            await UpdateServiceIfOutdatedAsync();
        };

        _opTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _opTimer.Tick += (_, _) =>
        {
            if (_currentCts != null)
                StatusText.Text = WpfLocalizer.T("Main_Op_Elapsed", _opName, (int)(DateTime.UtcNow - _opStart).TotalSeconds);
        };

        // background auto-renew co 12h; po przebiegu przeladuj liste (UI nie moze
        // trzymac nieaktualnych kopii - kolejny Upsert nadpisalby odnowiony cert).
        _bg = new BackgroundRenewalService(_renewal, _settings, new Progress<string>(m => Dispatcher.Invoke(() =>
        {
            LogBox.AppendText(m + "\n"); LogBox.ScrollToEnd();
        })), completed: () => Dispatcher.InvokeAsync(async () => await LoadCertsAsync()));
        SyncBackgroundRenewal();
    }

    /// <summary>
    /// Gdy dziala usluga Windows, to ona odnawia w tle - timer w GUI tylko by ja dublowal
    /// (wspolna blokada i tak nie pozwoli na rownolegle operacje).
    /// </summary>
    private void SyncBackgroundRenewal()
    {
        if (WindowsServiceManager.IsRunning)
        {
            _bg.Stop();
            LogBox.AppendText($"[Background] Działa usługa Windows {Certify.Service.ServiceInfo.Name} - odnawianie w tle prowadzi usługa.\n");
        }
        else _bg.Start();
    }

    /// <summary>
    /// Po aktualizacji aplikacji (ClickOnce / nowy build) usluga w Program Files ma stare pliki -
    /// podmien je, zeby usluga i GUI czytaly/zapisywaly JSON ta sama wersja kodu.
    /// </summary>
    private async Task UpdateServiceIfOutdatedAsync()
    {
        try
        {
            if (WindowsServiceManager.GetStatus() == null || !WindowsServiceManager.NeedsUpdate()) return;
        }
        catch { return; }
        // Usluga akurat odnawia - nie zatrzymujemy jej; sprobujemy przy kolejnym starcie GUI.
        if (_renewal.OperationLock.CurrentCount == 0)
        {
            LogBox.AppendText("[Service] Pliki usługi są nieaktualne, ale trwa operacja - aktualizacja przy następnym starcie lub w oknie Usługa Windows.\n");
            return;
        }
        await RunWithProgress("Service update", (log, ct) => Task.Run(() => WindowsServiceManager.Update(log), ct));
    }

    private void Service_Click(object sender, RoutedEventArgs e)
    {
        new ServiceWindow(RunWithProgress) { Owner = this }.ShowDialog();
        SyncBackgroundRenewal();
    }

    protected override void OnClosed(EventArgs e)
    {
        WpfLocalizer.LanguageChanged -= UpdateTitle;
        _bg.Dispose();
        base.OnClosed(e);
    }

    // Tytul z wersja skladany w kodzie (DynamicResource nie dokleja tekstu) - odswiezany po zmianie jezyka.
    private void UpdateTitle() => Title = $"{WpfLocalizer.T("Win_Main_Title")}  v{AppInfo.Version}";

    private async Task LoadCertsAsync()
    {
        var certs = await _store.LoadAllAsync();
        _viewModels.Clear();
        foreach (var c in certs.OrderBy(x => x.Name))
            _viewModels.Add(new ManagedCertificateViewModel(c));
        StatusBarText.Text = WpfLocalizer.T("Main_Loaded", certs.Count);
        DataDirText.Text = _settings.DataDirectory;
        DataDirText.ToolTip = _settings.DataDirectory;
        try { HeaderBadge.Text = certs.Count.ToString(); SidebarCountText.Text = WpfLocalizer.T("Main_Nav_Count", certs.Count); } catch { }
        UpdateStatusPanel();
        UpdateAlerts(certs);
    }

    private void UpdateAlerts(List<ManagedCertificate> certs)
    {
        var alerts = AlertsBuilder.Build(certs, _settings.ExpiryWarningDays);
        if (alerts.HasAny)
        {
            AlertsBanner.Visibility = Visibility.Visible;
            AlertsText.Text = alerts.Summary(_settings.ExpiryWarningDays);
        }
        else
        {
            AlertsBanner.Visibility = Visibility.Collapsed;
        }
    }

    private async void Alerts_Click(object sender, RoutedEventArgs e)
    {
        var certs = await _store.LoadAllAsync();
        new AlertsWindow(AlertsBuilder.Build(certs, _settings.ExpiryWarningDays), _settings.ExpiryWarningDays) { Owner = this }.ShowDialog();
    }

    private ManagedCertificate? SelectedCert => (CertsGrid.SelectedItem as ManagedCertificateViewModel)?.Model;

    /// <summary>
    /// Edycja/usuwanie w trakcie operacji (takze odnawiania w tle) gubily zmiany:
    /// operacja zapisywala potem swoja kopie certu. Blokujemy do jej konca.
    /// </summary>
    private bool IsBusy()
    {
        if (_currentCts == null && _renewal.OperationLock.CurrentCount > 0) return false;
        MessageBox.Show(WpfLocalizer.T("Main_Op_Busy", _currentCts != null ? _opName : "Auto-renew"));
        return true;
    }

    private async Task SaveCertLockedAsync(Func<Task> save)
    {
        await _renewal.OperationLock.WaitAsync();
        try { await save(); }
        finally { _renewal.OperationLock.Release(); }
        await LoadCertsAsync();
    }

    private async void NewCert_Click(object sender, RoutedEventArgs e)
    {
        if (IsBusy()) return;
        var cert = new ManagedCertificate { Name = WpfLocalizer.T("Main_Msg_NewCert"), EmailAddress = _settings.DefaultEmail ?? "" };
        var dlg = new CertificateEditWindow(cert, _settings, _deployers) { Owner = this };
        if (dlg.ShowDialog() == true)
            await SaveCertLockedAsync(() => _store.UpsertAsync(cert));
    }

    private async void EditCert_Click(object sender, RoutedEventArgs e)
    {
        var cert = SelectedCert;
        if (cert == null) { MessageBox.Show(WpfLocalizer.T("Msg_SelectCert")); return; }
        if (IsBusy()) return;
        var dlg = new CertificateEditWindow(cert, _settings, _deployers) { Owner = this };
        if (dlg.ShowDialog() == true)
            await SaveCertLockedAsync(() => _store.UpsertAsync(cert));
    }

    private void CertsGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e) => EditCert_Click(sender, e);

    private void CertsGrid_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e) => UpdateStatusPanel();

    private void UpdateStatusPanel()
    {
        var cert = SelectedCert;
        if (cert == null)
        {
            DetailName.Text = WpfLocalizer.T("Main_Panel_NoSel");
            DetailBadgeText.Text = "-";
            DetailAutoRenew.Text = DetailLastRenewal.Text = DetailExpiry.Text = "-";
            DetailNextRenewal.Text = DetailLastCa.Text = DetailLifetime.Text = "-";
            LifetimeBar.Value = 0;
            return;
        }
        DetailName.Text = string.IsNullOrWhiteSpace(cert.Name) ? cert.PrimaryDomain : cert.Name;
        DetailBadgeText.Text = cert.Status switch
        {
            CertificateStatus.Valid => WpfLocalizer.T("Badge_Active"),
            CertificateStatus.Expired => WpfLocalizer.T("Badge_Expired"),
            CertificateStatus.Error => WpfLocalizer.T("Badge_Error"),
            CertificateStatus.PendingValidation => WpfLocalizer.T("Badge_Pending"),
            CertificateStatus.Revoked => WpfLocalizer.T("Badge_Revoked"),
            _ => WpfLocalizer.T("Badge_NotStarted")
        };
        DetailAutoRenew.Text = cert.RenewalMode == RenewalMode.Auto ? WpfLocalizer.T("Yes") : WpfLocalizer.T("No");
        DetailLastRenewal.Text = cert.DateRenewed?.ToLocalTime().ToString("yyyy-MM-dd") ?? "-";
        DetailExpiry.Text = cert.DateExpiry == null
            ? "-"
            : WpfLocalizer.T("Main_ExpiryIn", cert.DateExpiry.Value.ToLocalTime().ToString("yyyy-MM-dd"), cert.DaysUntilExpiry);
        DetailNextRenewal.Text = cert.NextPlannedRenewal?.ToLocalTime().ToString("yyyy-MM-dd") ?? "-";
        DetailLastCa.Text = cert.CaDisplayName;
        if (cert.ElapsedLifetimePercent is { } pct)
        {
            LifetimeBar.Value = pct;
            DetailLifetime.Text = $"{pct:F0} %";
        }
        else
        {
            LifetimeBar.Value = 0;
            DetailLifetime.Text = "-";
        }
    }

    private void History_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedCert == null) { MessageBox.Show(WpfLocalizer.T("Msg_SelectCert")); return; }
        new HistoryWindow(SelectedCert) { Owner = this }.ShowDialog();
    }

    private void OpenCertLog_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var todayLog = Path.Combine(_settings.LogsDirectory, $"log-{DateTime.Now:yyyy-MM-dd}.txt");
            var target = File.Exists(todayLog) ? todayLog : _settings.LogsDirectory;
            Directory.CreateDirectory(_settings.LogsDirectory);
            Process.Start(new ProcessStartInfo("explorer.exe", target) { UseShellExecute = true });
        }
        catch { }
    }

    private async void DeleteCert_Click(object sender, RoutedEventArgs e)
    {
        var cert = SelectedCert;
        if (cert == null) return;
        if (IsBusy()) return;
        if (MessageBox.Show(WpfLocalizer.T("Main_Msg_DeleteConfirm", cert.Name), WpfLocalizer.T("Dlg_ConfirmTitle"), MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
        await SaveCertLockedAsync(() => _store.DeleteAsync(cert.Id));
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await LoadCertsAsync();

    // Operacje lapia wybrany cert na starcie: siatka pozostaje aktywna w trakcie
    // operacji, a SelectedCert czytany pozniej wskazywal na inny cert po zmianie zaznaczenia.
    private async void RequestCert_Click(object sender, RoutedEventArgs e)
    {
        var cert = SelectedCert;
        if (cert == null) { MessageBox.Show(WpfLocalizer.T("Msg_SelectCert")); return; }
        await RunWithProgress("Request", async (log, ct) =>
        {
            log.Report($"=== REQUEST dla {cert.Name} ===");
            cert.LastAttemptedCa = cert.CertificateAuthority.ToString();
            var taskRunner = new CertificateTaskRunner();
            if (!await taskRunner.RunPreRequestTasksAsync(cert, log, ct))
            {
                cert.StatusMessage = WpfLocalizer.T("Renew_Msg_PreFail") + " - " + WpfLocalizer.T("Main_Op_Aborted");
                await _store.UpsertAsync(cert);
                log.Report("=== PRZERWANO: pre-request task nie powiódł się ===");
                await Dispatcher.InvokeAsync(async () => await LoadCertsAsync());
                return;
            }
            var result = await _acme.RequestCertificateAsync(cert, log, ct);
            if (result.Success)
            {
                log.Report("=== Sukces, wdrażam ===");
                CertificateHistoryLog.Add(cert, HistoryKind.Request, true, WpfLocalizer.T("Hist_Msg_Requested"));
                var deployError = await _renewal.DeployAsync(cert, log, ct);
                if (deployError != null)
                {
                    cert.StatusMessage = WpfLocalizer.T("Deploy_Msg_Failed", deployError);
                    CertificateHistoryLog.Add(cert, HistoryKind.Deploy, false, deployError);
                }
                await taskRunner.RunDeploymentTasksAsync(cert, certSuccess: true, log, ct);
                await _store.UpsertAsync(cert);
                log.Report(deployError == null ? "=== Zakończono pomyślnie ===" : $"=== Certyfikat wystawiony, BŁĄD wdrożenia: {deployError} ===");
            }
            else
            {
                await taskRunner.RunDeploymentTasksAsync(cert, certSuccess: false, log, ct);
                CertificateHistoryLog.Add(cert, HistoryKind.Request, false, result.Message);
                await _store.UpsertAsync(cert);
                log.Report($"=== BŁĄD: {result.Message} ===");
                if (_email.Enabled)
                {
                    var (subject, body) = EmailNotifier.BuildFailure(
                        cert.Name, string.Join(",", cert.Domains), result.Message);
                    var (ok, msg) = await _email.SendAsync(subject, body);
                    log.Report(ok ? "[Email] Powiadomienie o bledzie wyslane." : $"[Email] BLAD wysylki: {msg}");
                }
            }
            await Dispatcher.InvokeAsync(async () => await LoadCertsAsync());
        });
    }

    private async void TestCert_Click(object sender, RoutedEventArgs e)
    {
        var cert = SelectedCert;
        if (cert == null) { MessageBox.Show(WpfLocalizer.T("Msg_SelectCert")); return; }
        await RunWithProgress("Staging Test", async (log, ct) =>
        {
            var clone = CloneForStaging(cert);
            if (clone == null)
            {
                log.Report($"=== TEST pominięty: {CertificateAuthorityCatalog.DisplayName(cert.CertificateAuthority)} nie ma publicznego staging. ===");
                return;
            }
            log.Report($"=== TEST (STAGING) dla {clone.Name} ===");
            var result = await _acme.RequestCertificateAsync(clone, log, ct);
            log.Report(result.Success ? "=== TEST OK (staging cert wystawiony) ===" : $"=== TEST BŁĄD: {result.Message} ===");
            // don't save staging clone, ale zapisz skad byla ostatnia proba (jak CTW "Last Attempted CA")
            cert.LastAttemptedCa = clone.CertificateAuthority.ToString();
            CertificateHistoryLog.Add(cert, HistoryKind.Test, result.Success, result.Success ? WpfLocalizer.T("Hist_Msg_StagingOk") : result.Message);
            await _store.UpsertAsync(cert);
        });
    }

    private async void DeployCert_Click(object sender, RoutedEventArgs e)
    {
        var cert = SelectedCert;
        if (cert == null) { MessageBox.Show(WpfLocalizer.T("Msg_SelectCert")); return; }
        await RunWithProgress("Deploy", async (log, ct) =>
        {
            if (string.IsNullOrWhiteSpace(cert.PfxPath) || !File.Exists(cert.PfxPath!))
            { log.Report("Brak PFX - najpierw Request"); return; }
            var deployError = await _renewal.DeployAsync(cert, log, ct);
            await new CertificateTaskRunner().RunDeploymentTasksAsync(cert, certSuccess: true, log, ct);
            CertificateHistoryLog.Add(cert, HistoryKind.Deploy, deployError == null,
                deployError ?? WpfLocalizer.T("Hist_Msg_Deployed"));
            await _store.UpsertAsync(cert);
            log.Report(deployError == null ? "Deploy zakończony" : $"Deploy BŁĄD: {deployError}");
        });
    }

    private async void ExportCert_Click(object sender, RoutedEventArgs e)
    {
        var cert = SelectedCert;
        if (cert == null) { MessageBox.Show(WpfLocalizer.T("Msg_SelectCert")); return; }
        if (string.IsNullOrWhiteSpace(cert.CertPath) || !File.Exists(cert.CertPath!))
        { MessageBox.Show(WpfLocalizer.T("Main_Msg_NoCertIssued")); return; }
        var dlg = new ExportCertificateWindow(cert) { Owner = this };
        if (dlg.ShowDialog() != true) return;
        await RunWithProgress("Export", async (log, ct) =>
        {
            var exporter = new CertificateExporter();
            var res = await exporter.ExportAsync(cert, dlg.SelectedFormat, dlg.DestinationPath, ct);
            log.Report(res.Success
                ? $"[Export] Zapisano {dlg.SelectedFormat.DisplayName()} -> {res.ExportedPath}"
                : $"[Export] BŁĄD: {res.Message}");
            CertificateHistoryLog.Add(cert, HistoryKind.Export, res.Success,
                res.Success ? WpfLocalizer.T("Hist_Msg_Exported", dlg.SelectedFormat, res.ExportedPath) : res.Message);
            await _store.UpsertAsync(cert);
        });
    }

    private async void RenewAll_Click(object sender, RoutedEventArgs e) => await RunWithProgress("Renew All", async (log, ct) =>
    {
        log.Report("=== RENEW ALL (sprawdzam terminy) ===");
        await _renewal.CheckAndRenewAllAsync(log, ct);
        await Dispatcher.InvokeAsync(async () => await LoadCertsAsync());
        log.Report("=== RenewAll zakończony ===");
    });

    private ManagedCertificate? CloneForStaging(ManagedCertificate src)
    {
        if (!CertificateAuthorityCatalog.TryGetStaging(src.CertificateAuthority, out var staging))
            return null; // np. ZeroSSL nie ma publicznego staging
        var clone = new ManagedCertificate
        {
            // Osobny katalog certs/<id>-staging: z tym samym Id writer nadpisywal
            // produkcyjny PFX/klucz certyfikatem staging (niezaufanym).
            Id = src.Id + "-staging",
            Name = src.Name,
            Domains = new List<string>(src.Domains),
            CertificateAuthority = staging,
            ChallengeType = src.ChallengeType,
            EmailAddress = src.EmailAddress,
            EabKeyId = src.EabKeyId,
            EabHmacKey = src.EabHmacKey,
            CustomAcmeDirectoryUrl = src.CustomAcmeDirectoryUrl,
            ChallengeConfig = src.ChallengeConfig,
            DeploymentTargets = new List<DeploymentTarget>(src.DeploymentTargets),
            RenewalDaysBeforeExpiry = src.RenewalDaysBeforeExpiry
        };
        return clone;
    }

    private async Task RunWithProgress(string opName, Func<IProgress<string>, CancellationToken, Task> action)
    {
        // In Progress: jedna operacja naraz (jak CTW zakladka In Progress).
        if (_currentCts != null) { MessageBox.Show(WpfLocalizer.T("Main_Op_Busy", _opName)); return; }
        // Wspolna blokada z odnawianiem w tle (to samo co Renew All, tylko z timera).
        if (!_renewal.OperationLock.Wait(0)) { MessageBox.Show(WpfLocalizer.T("Main_Op_Busy", "Auto-renew")); return; }
        _currentCts = new CancellationTokenSource();
        _opName = opName;
        _opStart = DateTime.UtcNow;
        ProgressBar.Visibility = Visibility.Visible;
        ProgressBar.IsIndeterminate = true;
        CancelButton.Visibility = Visibility.Visible;
        StatusText.Text = $"{opName}...";
        _opTimer.Start();
        LogBox.AppendText($"[{DateTime.Now:HH:mm:ss}] Start: {opName}...\n");
        LogBox.ScrollToEnd();
        IProgress<string> progress = new Progress<string>(msg =>
        {
            Dispatcher.Invoke(() =>
            {
                LogBox.AppendText(msg + "\n");
                LogBox.ScrollToEnd();
                StatusBarText.Text = msg.Length > 120 ? msg[..120] : msg;
                // also file log
                try { File.AppendAllText(Path.Combine(_settings.DataDirectory, "logs", $"log-{DateTime.Now:yyyy-MM-dd}.txt"), $"[{DateTime.Now:HH:mm:ss}] {msg}\n"); } catch { }
            });
        });
        try
        {
            await action(progress, _currentCts.Token);
        }
        catch (OperationCanceledException)
        {
            progress.Report("Anulowano przez użytkownika.");
        }
        catch (Exception ex)
        {
            progress.Report($"Wyjątek: {ex}");
        }
        finally
        {
            _opTimer.Stop();
            var cts = _currentCts;
            _currentCts = null;
            cts?.Dispose();
            _renewal.OperationLock.Release();
            ProgressBar.IsIndeterminate = false;
            ProgressBar.Visibility = Visibility.Collapsed;
            CancelButton.Visibility = Visibility.Collapsed;
            StatusText.Text = WpfLocalizer.T("Main_Status_Ready");
        }
    }

    private void CancelOperation_Click(object sender, RoutedEventArgs e)
    {
        try { _currentCts?.Cancel(); } catch { }
    }

    private void PreviewCert_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedCert == null) { MessageBox.Show(WpfLocalizer.T("Msg_SelectCert")); return; }
        new PreviewWindow(SelectedCert) { Owner = this }.ShowDialog();
    }

    private async void Backup_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Title = WpfLocalizer.T("Main_Btn_Backup") + " CertifyApp",
            Filter = WpfLocalizer.T("Main_Filter_Backup"),
            FileName = $"certify-backup-{DateTime.Now:yyyy-MM-dd}.cbak"
        };
        if (dlg.ShowDialog(this) != true) return;
        string? password = null;
        if (dlg.FileName.EndsWith(".cbak", StringComparison.OrdinalIgnoreCase))
        {
            var pw = new PasswordWindow(WpfLocalizer.T("Pw_Prompt_Encrypt"), confirm: true) { Owner = this };
            if (pw.ShowDialog() != true) return;
            password = pw.Password;
        }
        await RunWithProgress("Backup", async (log, ct) =>
        {
            var res = await new BackupService(_settings, _store).ExportAsync(dlg.FileName, log, ct, password);
            log.Report(res.Success ? $"[Backup] OK: {res.Message}" : $"[Backup] BLAD: {res.Message}");
        });
    }

    private async void Import_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = WpfLocalizer.T("Win_Import_Title") + " CertifyApp",
            Filter = "Backup (*.cbak;*.zip)|*.cbak;*.zip"
        };
        if (dlg.ShowDialog(this) != true) return;
        var svc = new BackupService(_settings, _store);
        string? password = null;
        if (BackupEncryption.IsEncrypted(dlg.FileName))
        {
            var pw = new PasswordWindow(WpfLocalizer.T("Pw_Prompt_Decrypt"), confirm: false) { Owner = this };
            if (pw.ShowDialog() != true) return;
            password = pw.Password;
        }
        var peek = svc.Peek(dlg.FileName, password);
        if (!peek.Success) { MessageBox.Show(this, peek.Message, WpfLocalizer.T("Main_Btn_Import"), MessageBoxButton.OK, MessageBoxImage.Warning); return; }
        var win = new ImportWindow(dlg.FileName, peek.Manifest!) { Owner = this };
        if (win.ShowDialog() != true) return;
        await RunWithProgress("Import", async (log, ct) =>
        {
            var res = await svc.ImportAsync(dlg.FileName, win.SelectedMode, log, ct, password);
            log.Report(res.Success ? $"[Backup] OK: {res.Message}" : $"[Backup] BLAD: {res.Message}");
            if (res.Success && win.SelectedMode == BackupImportMode.Replace)
            {
                // Replace zapisal ustawienia z backupu na dysk - wczytaj je do pamieci,
                // inaczej kolejny zapis z okna Ustawien nadpisalby je starymi wartosciami.
                _settings.CopyFrom(_settingsStore.Load());
                WpfLocalizer.Apply(_settings);
            }
            await Dispatcher.InvokeAsync(async () => await LoadCertsAsync());
        });
    }

    private async void TaskScheduler_Click(object sender, RoutedEventArgs e) => await RunWithProgress("Task Scheduler", async (log, ct) =>
    {
        await Task.Run(() => TaskSchedulerHelper.TryCreateTask(log), ct);
        log.Report($"Polecenie: {TaskSchedulerHelper.GetCreateCommand()}");
    });

    private void ClearLog_Click(object sender, RoutedEventArgs e) => LogBox.Clear();

    private void SaveLog_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Title = WpfLocalizer.T("Main_Btn_SaveLog"),
            Filter = WpfLocalizer.T("Main_Filter_Log"),
            FileName = $"certify-log-{DateTime.Now:yyyy-MM-dd_HHmmss}.txt"
        };
        if (dlg.ShowDialog(this) != true) return;
        try
        {
            // UTF-8 z BOM, zeby Notatnik/PowerShell 5.1 poprawnie pokazaly polskie znaki
            File.WriteAllText(dlg.FileName, LogBox.Text, new System.Text.UTF8Encoding(true));
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, WpfLocalizer.T("Main_Msg_LogSaveFailed", ex.Message), WpfLocalizer.T("Main_Btn_SaveLog"), MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async void Calendar_Click(object sender, RoutedEventArgs e)
    {
        var certs = await _store.LoadAllAsync();
        new CalendarWindow(certs) { Owner = this }.ShowDialog();
    }

    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new SettingsWindow(_settings) { Owner = this };
        if (dlg.ShowDialog() == true)
        {
            _settings.CopyFrom(dlg.Result);
            _settingsStore.Save(_settings);
            WpfLocalizer.Apply(_settings);
            StatusBarText.Text = WpfLocalizer.T("Main_Msg_SettingsSaved");
        }
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        try { Directory.CreateDirectory(_settings.DataDirectory); Process.Start(new ProcessStartInfo("explorer.exe", _settings.DataDirectory) { UseShellExecute = true }); } catch { }
    }
}

public class ManagedCertificateViewModel
{
    public ManagedCertificate Model { get; }
    public ManagedCertificateViewModel(ManagedCertificate m) => Model = m;
    public string Name => Model.Name;
    public string DomainsDisplay => string.Join(", ", Model.Domains);
    public string CertificateAuthority => Model.CertificateAuthority.ToString();
    public string ChallengeType => Model.ChallengeType.ToString();
    public string DeploymentDisplay => string.Join(", ", Model.DeploymentTargets.Select(d => d.TargetType));
    public string Status => Model.Status.ToString();
    public string StatusMessage => Model.StatusMessage;
    public string DateExpiryDisplay => Model.DateExpiry?.ToLocalTime().ToString("yyyy-MM-dd HH:mm") ?? "-";
    public int DaysUntilExpiry => Model.DaysUntilExpiry == int.MaxValue ? 999 : Model.DaysUntilExpiry;
}
