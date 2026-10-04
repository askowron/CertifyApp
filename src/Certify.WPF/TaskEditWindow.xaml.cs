using System.IO;
using System.Windows;
using System.Windows.Controls;
using Certify.Core.Models;
using Microsoft.Win32;

namespace Certify.WPF;

public partial class TaskEditWindow : Window
{
    private readonly CertificateTaskDefinition _task;
    private readonly Dictionary<string, FrameworkElement> _inputs = new();

    private static readonly CertificateTaskType[] TypesInOrder =
    [
        CertificateTaskType.ExportCertificate,
        CertificateTaskType.RestartService,
        CertificateTaskType.RunPowerShellScript,
        CertificateTaskType.RunProgram,
        CertificateTaskType.Wait,
        CertificateTaskType.Webhook
    ];

    public TaskEditWindow(CertificateTaskDefinition task)
    {
        InitializeComponent();
        _task = task;

        NameBox.Text = _task.Name;
        TypeBox.ItemsSource = TypesInOrder.Select(CertificateTaskParameters.TaskTypeDisplay).ToList();
        TypeBox.SelectedIndex = Math.Max(0, Array.IndexOf(TypesInOrder, _task.TaskType));
        StageBox.ItemsSource = Enum.GetValues<CertificateTaskStage>().Select(CertificateTaskParameters.StageDisplay).ToList();
        StageBox.SelectedIndex = (int)_task.Stage;
        TriggerBox.ItemsSource = Enum.GetValues<CertificateTaskTrigger>().Select(CertificateTaskParameters.TriggerDisplay).ToList();
        TriggerBox.SelectedIndex = (int)_task.Trigger;
        EnabledCheck.IsChecked = _task.Enabled;

        LastRunText.Text = _task.LastStatus == CertificateTaskLastStatus.NeverRun
            ? WpfLocalizer.T("Task_NeverRun")
            : WpfLocalizer.T("Task_LastRun", CertificateTaskParameters.StatusDisplay(_task.LastStatus, _task.LastRunUtc, _task.LastMessage));

        RebuildParams();
    }

    private void TypeBox_SelectionChanged(object sender, SelectionChangedEventArgs e) => RebuildParams();

    private void StageBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // Trigger ma sens tylko dla Deployment (Pre-Request zawsze leci przed requestem).
        var isPre = StageBox.SelectedIndex == (int)CertificateTaskStage.PreRequest;
        TriggerBox.IsEnabled = !isPre;
        if (isPre) TriggerBox.SelectedIndex = (int)CertificateTaskTrigger.Always;
    }

    private CertificateTaskType CurrentType =>
        TypeBox.SelectedIndex >= 0 ? TypesInOrder[TypeBox.SelectedIndex] : CertificateTaskType.ExportCertificate;

    private void RebuildParams()
    {
        ParamsPanel.Children.Clear();
        _inputs.Clear();
        foreach (var d in CertificateTaskParameters.GetDescriptors(CurrentType))
        {
            var row = new Grid { Margin = new Thickness(0, 0, 0, 10) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(140) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            if (d.Browse != TaskParamBrowse.None)
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var label = new TextBlock
            {
                Text = d.Label + (d.Required ? " *" : ""),
                Foreground = (System.Windows.Media.Brush)FindResource("TextSecondaryBrush"),
                FontSize = 12,
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(label, 0);
            row.Children.Add(label);

            _task.Parameters.TryGetValue(d.Key, out var current);
            var initial = string.IsNullOrWhiteSpace(current) ? d.Default ?? "" : current;

            FrameworkElement input;
            if (d.Options != null)
            {
                var cb = new ComboBox { IsEditable = true, Margin = new Thickness(0, 0, 0, 0) };
                cb.ItemsSource = d.Options.ToList();
                cb.Text = initial;
                input = cb;
            }
            else
            {
                input = new TextBox { Text = initial };
            }
            Grid.SetColumn(input, 1);
            row.Children.Add(input);
            _inputs[d.Key] = input;

            if (d.Browse != TaskParamBrowse.None)
            {
                var btn = new Button { Content = "...", Width = 36, Margin = new Thickness(8, 0, 0, 0) };
                var desc = d;
                btn.Click += (_, _) => BrowseParam(desc, input);
                Grid.SetColumn(btn, 2);
                row.Children.Add(btn);
            }
            ParamsPanel.Children.Add(row);
        }
    }

    private void BrowseParam(TaskParameterDescriptor d, FrameworkElement input)
    {
        string? current = input switch
        {
            TextBox tb => tb.Text,
            ComboBox cb => cb.Text,
            _ => null
        };
        if (d.Browse == TaskParamBrowse.Folder)
        {
            var dlg = new OpenFolderDialog { Title = d.Label };
            if (!string.IsNullOrWhiteSpace(current) && Directory.Exists(current))
                dlg.InitialDirectory = current;
            if (dlg.ShowDialog(this) == true) SetInput(input, dlg.FolderName);
        }
        else if (d.Browse == TaskParamBrowse.OpenFile)
        {
            var dlg = new OpenFileDialog { Title = d.Label, Filter = d.FileFilter ?? "All files (*.*)|*.*" };
            if (!string.IsNullOrWhiteSpace(current) && File.Exists(current))
                dlg.FileName = current;
            if (dlg.ShowDialog(this) == true) SetInput(input, dlg.FileName);
        }
        else if (d.Browse == TaskParamBrowse.SaveFile)
        {
            var dlg = new SaveFileDialog
            {
                Title = d.Label,
                Filter = d.FileFilter ?? "All files (*.*)|*.*",
                FileName = Path.GetFileName(current ?? ""),
                OverwritePrompt = true
            };
            if (dlg.ShowDialog(this) == true) SetInput(input, dlg.FileName);
        }
    }

    private static void SetInput(FrameworkElement input, string value)
    {
        if (input is TextBox tb) tb.Text = value;
        else if (input is ComboBox cb) cb.Text = value;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(NameBox.Text)) { MessageBox.Show(this, WpfLocalizer.T("Task_Msg_NameReq")); return; }
        var type = CurrentType;
        // Zbieraj do slownika roboczego - blad walidacji w polowie nie moze
        // zostawic czesciowo nadpisanych parametrow po Anuluj.
        var newParams = new Dictionary<string, string>();
        foreach (var d in CertificateTaskParameters.GetDescriptors(type))
        {
            var val = (_inputs[d.Key] switch
            {
                TextBox tb => tb.Text,
                ComboBox cb => cb.Text,
                _ => ""
            }).Trim();
            if (d.Required && string.IsNullOrWhiteSpace(val))
            {
                MessageBox.Show(this, WpfLocalizer.T("Task_Msg_ParamReq", d.Label));
                return;
            }
            if (d.Key == CertificateTaskParameters.P_Seconds &&
                (!int.TryParse(val, out var sec) || sec < 0))
            {
                MessageBox.Show(this, WpfLocalizer.T("Task_Msg_BadSeconds"));
                return;
            }
            newParams[d.Key] = val;
        }
        foreach (var kv in newParams) _task.Parameters[kv.Key] = kv.Value;
        _task.Name = NameBox.Text.Trim();
        _task.TaskType = type;
        _task.Stage = (CertificateTaskStage)StageBox.SelectedIndex;
        _task.Trigger = TriggerBox.IsEnabled
            ? (CertificateTaskTrigger)TriggerBox.SelectedIndex
            : CertificateTaskTrigger.Always;
        _task.Enabled = EnabledCheck.IsChecked == true;
        DialogResult = true;
    }
}
