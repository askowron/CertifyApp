using System.IO;
using System.Windows;
using Certify.Core.Models;
using Certify.Core.Services;
using Microsoft.Win32;

namespace Certify.WPF;

public partial class ExportCertificateWindow : Window
{
    private readonly ManagedCertificate _cert;

    public CertificateExportFormat SelectedFormat { get; private set; } = CertificateExportFormat.PemFullChainExcludingKey;
    public string DestinationPath { get; private set; } = string.Empty;

    private static readonly CertificateExportFormat[] FormatsInCtwOrder =
    [
        CertificateExportFormat.PemPrimary,
        CertificateExportFormat.PemIntermediateWithRoot,
        CertificateExportFormat.PemIntermediateOnly,
        CertificateExportFormat.PemPrivateKey,
        CertificateExportFormat.PemFullChainExcludingKey,
        CertificateExportFormat.PemFullChainIncludingKey,
        CertificateExportFormat.PemPrimaryWithIntermediate,
        CertificateExportFormat.Pfx
    ];

    public ExportCertificateWindow(ManagedCertificate cert)
    {
        InitializeComponent();
        _cert = cert;
        HeaderCertName.Text = string.IsNullOrWhiteSpace(cert.Name) ? cert.PrimaryDomain : cert.Name;
        FormatBox.ItemsSource = FormatsInCtwOrder.Select(f => f.DisplayName()).ToList();
        FormatBox.SelectedIndex = 4; // Full Chain (najczestszy wybor)
        UpdateDefaultPath();
    }

    private void FormatBox_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (FormatBox.SelectedIndex >= 0)
            SelectedFormat = FormatsInCtwOrder[FormatBox.SelectedIndex];
        UpdateDefaultPath();
    }

    private void UpdateDefaultPath()
    {
        var dir = !string.IsNullOrWhiteSpace(_cert.CertPath)
            ? Path.GetDirectoryName(_cert.CertPath)!
            : Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        var file = $"{CertificateArtifactWriter.SafeFileName(_cert.PrimaryDomain)}{SelectedFormat.DefaultExtension()}";
        DestBox.Text = Path.Combine(dir, file);
        HintText.Text = SelectedFormat == CertificateExportFormat.PemPrivateKey
            ? WpfLocalizer.T("Export_Hint_Key")
            : WpfLocalizer.T("Export_Hint_Local");
    }

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new SaveFileDialog
        {
            Filter = SelectedFormat.DialogFilter(),
            FileName = Path.GetFileName(DestBox.Text),
            InitialDirectory = Directory.Exists(Path.GetDirectoryName(DestBox.Text))
                ? Path.GetDirectoryName(DestBox.Text)
                : Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            OverwritePrompt = true
        };
        if (dlg.ShowDialog(this) == true)
            DestBox.Text = dlg.FileName;
    }

    private void Export_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(DestBox.Text))
        {
            MessageBox.Show(this, WpfLocalizer.T("Export_Msg_DestReq"), WpfLocalizer.T("Main_Btn_Export"), MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        DestinationPath = DestBox.Text;
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
