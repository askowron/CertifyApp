using System.Windows;
using System.Windows.Media;

namespace Certify.WPF;

/// <summary>
/// Zarzadzanie zadaniem Harmonogramu zadan (codzienne --renew jako SYSTEM) - odpowiednik ServiceWindow.
/// Operacje ida przez MainWindow.RunWithProgress (log w glownym oknie).
/// </summary>
public partial class SchedulerWindow : Window
{
    private readonly Func<string, Func<IProgress<string>, CancellationToken, Task>, Task> _run;
    private ScheduledTaskInfo? _info;

    public SchedulerWindow(Func<string, Func<IProgress<string>, CancellationToken, Task>, Task> run)
    {
        InitializeComponent();
        _run = run;
        NameValue.Text = $"{TaskSchedulerHelper.TaskName} (SYSTEM)";
        HourBox.Value = TaskSchedulerHelper.DefaultTime.Hours;
        MinuteBox.Value = TaskSchedulerHelper.DefaultTime.Minutes;
        Refresh(loadTime: true);
        HourBox.ValueChanged += (_, _) => UpdateButtons();
        MinuteBox.ValueChanged += (_, _) => UpdateButtons();
    }

    private TimeSpan SelectedTime => new(HourBox.Value, MinuteBox.Value, 0);

    private void Refresh(bool loadTime = false)
    {
        _info = TaskSchedulerHelper.GetInfo();
        if (loadTime && _info?.StartTime is { } t)
        {
            HourBox.Value = t.Hours;
            MinuteBox.Value = t.Minutes;
        }

        StatusValue.Text = _info switch
        {
            null => WpfLocalizer.T("Sched_Status_None"),
            { Running: true } => WpfLocalizer.T("Sched_Status_Running"),
            { Enabled: true } => WpfLocalizer.T("Sched_Status_Ready"),
            _ => WpfLocalizer.T("Sched_Status_Disabled")
        };
        StatusValue.Foreground = Brush(_info switch
        {
            null => "TextSecondaryBrush",
            { Enabled: true } => "SuccessBrush",
            _ => "WarningBrush"
        });

        LastRunValue.Text = _info == null ? "-"
            : TaskSchedulerHelper.HasNeverRun(_info) ? WpfLocalizer.T("Sched_Last_Never")
            : WpfLocalizer.T("Sched_Last_Result", _info.LastRun!.Value.ToString("g"),
                _info.LastResult == 0 ? "OK" : $"0x{_info.LastResult:X}");
        LastRunValue.Foreground = Brush(_info != null && !TaskSchedulerHelper.HasNeverRun(_info) && _info.LastResult != 0
            ? "DangerBrush" : "TextPrimaryBrush");
        NextRunValue.Text = _info?.NextRun?.ToString("g") ?? "-";

        var outdated = _info != null && !TaskSchedulerHelper.PointsToCurrentExe(_info);
        CommandValue.Text = _info == null ? TaskSchedulerHelper.ExePath + " --renew"
            : (_info.Command ?? "?") + (outdated ? "\n" + WpfLocalizer.T("Sched_Cmd_Outdated") : "");
        CommandValue.Foreground = Brush(outdated ? "WarningBrush" : "TextPrimaryBrush");
        UpdateButtons();
    }

    private void UpdateButtons()
    {
        if (CreateButton == null) return;
        var exists = _info != null;
        CreateButton.IsEnabled = !exists;
        // Aktualizuj = inna godzina albo inna sciezka exe (ClickOnce po aktualizacji).
        UpdateButton.IsEnabled = exists && (_info!.StartTime != SelectedTime || !TaskSchedulerHelper.PointsToCurrentExe(_info));
        RunButton.IsEnabled = exists && _info!.Enabled && !_info.Running;
        EnableButton.IsEnabled = exists;
        DeleteButton.IsEnabled = exists;
        var enabled = _info?.Enabled != false;
        EnableButton.SetResourceReference(ContentProperty, enabled ? "Sched_Btn_Disable" : "Sched_Btn_Enable");
        ButtonIcon.SetKind(EnableButton, enabled ? "Pause" : "Enable");
    }

    private Brush Brush(string key) => (Brush)FindResource(key);

    private async Task RunAsync(string opName, Func<IProgress<string>, bool> action)
    {
        IsEnabled = false;
        try { await _run(opName, (log, ct) => Task.Run(() => { action(log); }, ct)); }
        finally
        {
            IsEnabled = true;
            Refresh();
        }
    }

    private async void Create_Click(object sender, RoutedEventArgs e)
    {
        var time = SelectedTime;
        await RunAsync("Task Scheduler create", log => TaskSchedulerHelper.Create(time, log));
    }

    private async void Update_Click(object sender, RoutedEventArgs e)
    {
        var time = SelectedTime;
        var wasEnabled = _info?.Enabled != false;
        // /Create /F nadpisuje zadanie i je wlacza - zachowujemy wylaczone.
        await RunAsync("Task Scheduler update", log =>
            TaskSchedulerHelper.Create(time, log) && (wasEnabled || TaskSchedulerHelper.SetEnabled(false, log)));
    }

    private async void Run_Click(object sender, RoutedEventArgs e) =>
        await RunAsync("Task Scheduler run", TaskSchedulerHelper.RunNow);

    private async void Toggle_Click(object sender, RoutedEventArgs e)
    {
        var enable = _info?.Enabled == false;
        await RunAsync(enable ? "Task Scheduler enable" : "Task Scheduler disable", log => TaskSchedulerHelper.SetEnabled(enable, log));
    }

    private async void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show(this, WpfLocalizer.T("Sched_Msg_DeleteConfirm"), WpfLocalizer.T("Dlg_ConfirmTitle"), MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
        await RunAsync("Task Scheduler delete", TaskSchedulerHelper.Delete);
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
