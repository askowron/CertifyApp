using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Certify.Core.Models;

namespace Certify.WPF;

public partial class CalendarWindow : Window
{
    private readonly List<ManagedCertificate> _certs;
    private int _year;
    private int _month;

    public CalendarWindow(List<ManagedCertificate> certs)
    {
        InitializeComponent();
        _certs = certs;
        var now = DateTime.Now;
        _year = now.Year;
        _month = now.Month;
        Render();
    }

    private void Prev_Click(object sender, RoutedEventArgs e) => Shift(-1);
    private void Next_Click(object sender, RoutedEventArgs e) => Shift(1);
    private void Today_Click(object sender, RoutedEventArgs e)
    {
        var now = DateTime.Now;
        _year = now.Year;
        _month = now.Month;
        Render();
    }

    private void Shift(int months)
    {
        var d = new DateTime(_year, _month, 1).AddMonths(months);
        _year = d.Year;
        _month = d.Month;
        Render();
    }

    private void Render()
    {
        var cal = RenewalCalendar.Build(_certs, _year, _month);
        MonthTitle.Text = cal.GetTitle(Certify.Core.Localization.UIStrings.Lang);
        var dow = Certify.Core.Localization.UIStrings.T("Cal_Dow").Split(';');
        var boxes = new[] { Dow0, Dow1, Dow2, Dow3, Dow4, Dow5, Dow6 };
        for (var d = 0; d < 7 && d < dow.Length; d++) boxes[d].Text = dow[d];
        DaysGrid.Children.Clear();
        var today = DateTime.Now.Date;
        var shown = 0;

        foreach (var (day, idx) in cal.Days.Select((d, i) => (d, i)))
        {
            var cell = new Border
            {
                CornerRadius = new CornerRadius(8),
                Margin = new Thickness(3),
                Padding = new Thickness(6, 4, 6, 4),
                Background = day.Date == today
                    ? (Brush)FindResource("CardAltBrush")
                    : Brushes.Transparent,
                BorderBrush = day.Date == today
                    ? (Brush)FindResource("AccentBrush")
                    : Brushes.Transparent,
                BorderThickness = new Thickness(day.Date == today ? 1 : 0),
                Opacity = day.IsCurrentMonth ? 1 : 0.35
            };
            var stack = new StackPanel();
            stack.Children.Add(new TextBlock
            {
                Text = day.Date.Day.ToString(),
                FontSize = 12,
                FontWeight = FontWeights.SemiBold,
                Foreground = (Brush)FindResource("TextPrimaryBrush")
            });
            foreach (var e in day.Entries.Take(3))
            {
                stack.Children.Add(new TextBlock
                {
                    Text = (e.Kind == CalendarEntryKind.Expiry ? "● " : "○ ") + e.CertName,
                    FontSize = 11,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    ToolTip = e.CertName,
                    Foreground = (Brush)FindResource(e.Kind == CalendarEntryKind.Expiry ? "DangerBrush" : "WarningBrush")
                });
            }
            if (day.Entries.Count > 3)
            {
                stack.Children.Add(new TextBlock
                {
                    Text = $"+{day.Entries.Count - 3}",
                    FontSize = 11,
                    Foreground = (Brush)FindResource("TextMutedBrush")
                });
            }
            shown += day.Entries.Count;
            cell.Child = stack;
            Grid.SetRow(cell, idx / 7);
            Grid.SetColumn(cell, idx % 7);
            DaysGrid.Children.Add(cell);
        }
        CalendarFooter.Text = WpfLocalizer.T("Cal_Footer", shown);
    }

    private void Close_Click(object sender, RoutedEventArgs e) => DialogResult = true;
}
