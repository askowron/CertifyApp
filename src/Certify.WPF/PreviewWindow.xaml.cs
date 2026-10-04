using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Certify.Core.Models;

namespace Certify.WPF;

public partial class PreviewWindow : Window
{
    public PreviewWindow(ManagedCertificate cert)
    {
        InitializeComponent();
        var p = CertificatePreviewBuilder.Build(cert);

        PreviewName.Text = p.Name;
        PreviewDomains.Text = p.Domains.Count > 0 ? string.Join("\n", p.Domains) : "-";
        PreviewPrimary.Text = WpfLocalizer.T("Preview_Primary", p.PrimaryDomain, Math.Max(0, p.Domains.Count - 1), p.ExpiryDisplay);
        PreviewCa.Text = WpfLocalizer.T("Preview_Ca", p.CaDisplay) + (p.IsStaging ? WpfLocalizer.T("Preview_Ca_Staging") : "");
        PreviewDirectory.Text = p.DirectoryUrl;
        PreviewChallenge.Text = WpfLocalizer.T("Preview_Challenge", p.ChallengeDisplay);
        PreviewChallengeDetail.Text = p.ChallengeDetail;
        PreviewDeploy.Text = p.DeploymentDisplays.Count > 0
            ? WpfLocalizer.T("Preview_Depl", string.Join("  |  ", p.DeploymentDisplays))
            : WpfLocalizer.T("Preview_Depl_None");
        PreviewTasks.Text = WpfLocalizer.T("Preview_Tasks", p.PreTaskCount, p.DeploymentTaskCount);
        PreviewRenewal.Text = WpfLocalizer.T("Preview_Renewal", p.RenewalDisplay);

        foreach (var w in p.Warnings)
        {
            WarningsPanel.Children.Add(new TextBlock
            {
                Text = (w.IsError ? "\u2715 " : "\u2022 ") + w.Text,
                Foreground = (Brush)FindResource(w.IsError ? "DangerBrush" : "TextSecondaryBrush"),
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 6),
                FontWeight = w.IsError ? FontWeights.SemiBold : FontWeights.Normal
            });
        }
        if (p.Warnings.Count == 0)
        {
            WarningsPanel.Children.Add(new TextBlock
            {
                Text = WpfLocalizer.T("Preview_Ok"),
                Foreground = (Brush)FindResource("SuccessBrush"),
                FontSize = 12,
                FontWeight = FontWeights.SemiBold
            });
        }
        PreviewFooter.Text = p.HasErrors ? WpfLocalizer.T("Preview_Footer_Err") : WpfLocalizer.T("Preview_Footer_Ok");
    }

    private void Close_Click(object sender, RoutedEventArgs e) => DialogResult = true;
}
