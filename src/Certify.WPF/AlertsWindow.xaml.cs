using System.Windows;
using System.Windows.Controls;
using Certify.Core.Models;

namespace Certify.WPF;

public partial class AlertsWindow : Window
{
    public AlertsWindow(CertificateAlerts alerts, int warningDays)
    {
        InitializeComponent();
        AlertsSummary.Text = alerts.HasAny ? alerts.Summary(warningDays) : WpfLocalizer.T("Alerts_None_All");
        Fill(ExpiredList, alerts.Expired, WpfLocalizer.T("Alerts_None_Expired"));
        Fill(ExpiringList, alerts.ExpiringSoon, WpfLocalizer.T("Alerts_None_Expiring", warningDays));
        Fill(ErrorsList, alerts.Errors, WpfLocalizer.T("Alerts_None_Errors"));
        Fill(RenewalsList, alerts.UpcomingRenewals, WpfLocalizer.T("Alerts_None_Renewals"));
    }

    private static void Fill(ListBox list, List<AlertItem> items, string empty)
    {
        list.ItemsSource = items.Count > 0
            ? items.Select(i => new { Title = i.Title, Detail = $"{i.Title} - {i.Detail}" }).ToList()
            : new[] { new { Title = "", Detail = empty } };
    }

    private void Close_Click(object sender, RoutedEventArgs e) => DialogResult = true;
}
