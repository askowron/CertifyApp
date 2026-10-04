using System.Windows;
using Certify.Core.Models;

namespace Certify.WPF;

public partial class HistoryWindow : Window
{
    public HistoryWindow(ManagedCertificate cert)
    {
        InitializeComponent();
        HistoryCert.Text = string.IsNullOrWhiteSpace(cert.Name) ? cert.PrimaryDomain : cert.Name;
        var (total, ok, fail) = CertificateHistoryLog.Stats(cert);
        HistoryStats.Text = WpfLocalizer.T("History_Stats", total, ok, fail);
        HistoryList.ItemsSource = cert.History
            .OrderByDescending(h => h.TimestampUtc)
            .ToList();
    }

    private void Close_Click(object sender, RoutedEventArgs e) => DialogResult = true;
}
