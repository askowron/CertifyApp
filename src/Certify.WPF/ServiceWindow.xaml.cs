using System.ServiceProcess;
using System.Windows;
using Certify.Service;

namespace Certify.WPF;

/// <summary>
/// Zarzadzanie usluga Windows odnawiajaca certyfikaty. Operacje ida przez
/// MainWindow.RunWithProgress (log w glownym oknie + wspolna blokada operacji,
/// wiec usluga nie jest zatrzymywana w trakcie odnawiania).
/// </summary>
public partial class ServiceWindow : Window
{
    private readonly Func<string, Func<IProgress<string>, CancellationToken, Task>, Task> _run;

    public ServiceWindow(Func<string, Func<IProgress<string>, CancellationToken, Task>, Task> run)
    {
        InitializeComponent();
        _run = run;
        NameValue.Text = $"{ServiceInfo.Name} (LocalSystem)";
        DirValue.Text = ServiceInfo.InstallDirectory;
        Refresh();
    }

    private void Refresh()
    {
        var status = WindowsServiceManager.GetStatus();
        StatusValue.Text = status switch
        {
            null => WpfLocalizer.T("Svc_Status_NotInstalled"),
            ServiceControllerStatus.Running => WpfLocalizer.T("Svc_Status_Running"),
            ServiceControllerStatus.Stopped => WpfLocalizer.T("Svc_Status_Stopped"),
            _ => WpfLocalizer.T("Svc_Status_Pending", status)
        };
        StatusValue.Foreground = (System.Windows.Media.Brush)FindResource(status switch
        {
            ServiceControllerStatus.Running => "SuccessBrush",
            null => "TextSecondaryBrush",
            _ => "WarningBrush"
        });
        var outdated = status != null && WindowsServiceManager.NeedsUpdate();
        FilesValue.Text = status == null ? "-" : WpfLocalizer.T(outdated ? "Svc_Files_Outdated" : "Svc_Files_Current");
        FilesValue.Foreground = (System.Windows.Media.Brush)FindResource(outdated ? "WarningBrush" : "TextPrimaryBrush");

        InstallButton.IsEnabled = status == null;
        UpdateButton.IsEnabled = outdated;
        StartButton.IsEnabled = status == ServiceControllerStatus.Stopped;
        StopButton.IsEnabled = status == ServiceControllerStatus.Running;
        UninstallButton.IsEnabled = status != null;
    }

    private async Task RunAsync(string opName, Action<IProgress<string>> action)
    {
        IsEnabled = false;
        try { await _run(opName, (log, ct) => Task.Run(() => action(log), ct)); }
        finally
        {
            IsEnabled = true;
            Refresh();
        }
    }

    private async void Install_Click(object sender, RoutedEventArgs e) => await RunAsync("Service install", WindowsServiceManager.Install);
    private async void Update_Click(object sender, RoutedEventArgs e) => await RunAsync("Service update", WindowsServiceManager.Update);
    private async void Start_Click(object sender, RoutedEventArgs e) => await RunAsync("Service start", WindowsServiceManager.Start);
    private async void Stop_Click(object sender, RoutedEventArgs e) => await RunAsync("Service stop", WindowsServiceManager.Stop);

    private async void Uninstall_Click(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show(this, WpfLocalizer.T("Svc_Msg_UninstallConfirm"), WpfLocalizer.T("Dlg_ConfirmTitle"), MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
        await RunAsync("Service uninstall", WindowsServiceManager.Uninstall);
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
