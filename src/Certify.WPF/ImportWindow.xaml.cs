using System.Windows;
using System.Windows.Controls;
using Certify.Core.Services;

namespace Certify.WPF;

public partial class ImportWindow : Window
{
    private static (BackupImportMode Mode, string Display, string Hint)[] Modes =>
    [
        (BackupImportMode.Merge, WpfLocalizer.T("Import_Mode_Merge"), WpfLocalizer.T("Import_Mode_MergeHint")),
        (BackupImportMode.Replace, WpfLocalizer.T("Import_Mode_Replace"), WpfLocalizer.T("Import_Mode_ReplaceHint"))
    ];

    public BackupImportMode SelectedMode => Modes[ModeBox.SelectedIndex].Mode;

    public ImportWindow(string zipPath, BackupManifest manifest)
    {
        InitializeComponent();
        ImportFile.Text = System.IO.Path.GetFileName(zipPath);
        ManifestText.Text = WpfLocalizer.T("Import_Manifest", manifest.CertCount,
            manifest.ExportedAtUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm"), manifest.Machine,
            WpfLocalizer.T(manifest.HasAccounts ? "YesNo_Yes" : "YesNo_No"),
            WpfLocalizer.T(manifest.HasSettings ? "YesNo_Yes" : "YesNo_No"));
        ModeBox.ItemsSource = Modes.Select(m => m.Display).ToList();
        ModeBox.SelectedIndex = 0;
        ModeBox.SelectionChanged += (_, _) => ModeHint.Text = Modes[ModeBox.SelectedIndex].Hint;
        ModeHint.Text = Modes[0].Hint;
    }

    private void Import_Click(object sender, RoutedEventArgs e) => DialogResult = true;
    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
