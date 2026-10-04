using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace Certify.WPF;

/// <summary>
/// Pomoc kontekstowa ("Co to jest?"):
///  - F1 - opis elementu pod kursorem (albo z fokusem),
///  - Shift+F1 / przycisk Pomoc - tryb pomocy: kursor "?", nastepne klikniecie pokazuje opis
///    zamiast wykonac akcje; Esc anuluje.
/// Opis: local:Help.Topic="X" -> klucz UIStrings "Help_X"; bez Topic - ToolTip elementu;
/// szukane w gore drzewa od kliknietego elementu.
/// </summary>
public static class Help
{
    public static readonly DependencyProperty TopicProperty = DependencyProperty.RegisterAttached(
        "Topic", typeof(string), typeof(Help), new PropertyMetadata(null));

    public static string? GetTopic(DependencyObject d) => (string?)d.GetValue(TopicProperty);
    public static void SetTopic(DependencyObject d, string? value) => d.SetValue(TopicProperty, value);

    private static bool _mode;
    private static Popup? _popup;

    /// <summary>Jednorazowo w App.OnStartup - dziala w kazdym oknie aplikacji.</summary>
    public static void Register()
    {
        EventManager.RegisterClassHandler(typeof(Window), UIElement.PreviewKeyDownEvent, new KeyEventHandler(OnPreviewKeyDown));
        EventManager.RegisterClassHandler(typeof(Window), UIElement.PreviewMouseDownEvent, new MouseButtonEventHandler(OnPreviewMouseDown));
        // Deactivated to zwykle zdarzenie CLR (nie routed) - podpinamy przy Loaded kazdego okna.
        // Przelaczenie do innej aplikacji wylacza tryb pomocy (inaczej zostaje kursor "?").
        EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent, new RoutedEventHandler((s, _) =>
        {
            if (s is not Window w) return;
            w.Deactivated -= OnWindowDeactivated;
            w.Deactivated += OnWindowDeactivated;
            // Dymek ma StaysOpen=true - przy przesunieciu/zamknieciu okna zostalby "w powietrzu".
            w.LocationChanged -= OnWindowMoved;
            w.LocationChanged += OnWindowMoved;
            w.Closed -= OnWindowMoved;
            w.Closed += OnWindowMoved;
        }));
    }

    private static void OnWindowDeactivated(object? sender, EventArgs e)
    {
        ExitMode();
        ClosePopup();
    }

    private static void OnWindowMoved(object? sender, EventArgs e) => ClosePopup();

    public static void ToggleMode()
    {
        if (_mode) { ExitMode(); return; }
        ClosePopup();
        _mode = true;
        Mouse.OverrideCursor = Cursors.Help;
    }

    private static void ExitMode()
    {
        if (!_mode) return;
        _mode = false;
        Mouse.OverrideCursor = null;
    }

    private static void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not Window w) return;
        if (e.Key == Key.F1)
        {
            e.Handled = true;
            if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) { ToggleMode(); return; }
            ExitMode();
            var target = HitTest(w, Mouse.GetPosition(w)) ?? Keyboard.FocusedElement as DependencyObject;
            if (target != null) Show(w, target);
        }
        else if (e.Key == Key.Escape && (_mode || _popup?.IsOpen == true))
        {
            e.Handled = true;
            ExitMode();
            ClosePopup();
        }
    }

    private static void OnPreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        // Otwarty dymek zamyka nastepne klikniecie gdziekolwiek w oknie (klik dziala normalnie).
        if (!_mode) { ClosePopup(); return; }
        if (sender is not Window w) return;
        // Klik "zjedzony": przycisk nie wykona akcji, pole nie dostanie fokusu.
        e.Handled = true;
        ExitMode();
        var target = HitTest(w, e.GetPosition(w)) ?? e.OriginalSource as DependencyObject;
        if (target != null) Show(w, target);
    }

    /// <summary>
    /// Hit test wizualny zamiast Mouse.DirectlyOver / OriginalSource - te pomijaja wylaczone
    /// kontrolki (IsEnabled=false), a opis wyszarzonego przycisku tez sie przydaje.
    /// </summary>
    private static DependencyObject? HitTest(Window w, Point p)
    {
        if (p.X < 0 || p.Y < 0 || p.X > w.ActualWidth || p.Y > w.ActualHeight) return null;
        return VisualTreeHelper.HitTest(w, p)?.VisualHit;
    }

    private static void Show(Window w, DependencyObject target)
    {
        var (text, source) = Resolve(target);
        var title = TitleFor(source ?? target);
        ClosePopup();

        var panel = new StackPanel { MaxWidth = 380 };
        panel.Children.Add(new TextBlock
        {
            Text = "  " + (title ?? WpfLocalizer.T("Main_Btn_Help")),
            FontWeight = FontWeights.SemiBold,
            FontSize = 13,
            Foreground = Res("TextPrimaryBrush"),
            TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(0, 0, 0, 6),
            FontFamily = new FontFamily("Segoe UI Variable, Segoe UI, Segoe Fluent Icons, Segoe MDL2 Assets")
        });
        panel.Children.Add(new TextBlock
        {
            Text = text ?? WpfLocalizer.T("Help_None"),
            TextWrapping = TextWrapping.Wrap,
            TextTrimming = TextTrimming.None,
            FontSize = 12,
            Foreground = Res(text == null ? "TextSecondaryBrush" : "TextPrimaryBrush")
        });
        panel.Children.Add(new TextBlock
        {
            Text = WpfLocalizer.T("Help_Footer"),
            FontSize = 10,
            Foreground = Res("TextMutedBrush"),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 8, 0, 0)
        });

        _popup = new Popup
        {
            Child = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(0x14, 0x1E, 0x33)),
                BorderBrush = Res("AccentBrush"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(14, 10, 14, 10),
                Child = panel,
                FlowDirection = w.FlowDirection
            },
            PlacementTarget = w,
            Placement = PlacementMode.MousePoint,
            HorizontalOffset = 12,
            VerticalOffset = 12,
            AllowsTransparency = true,
            // StaysOpen=false zamykal dymek juz przy zwolnieniu przycisku myszy, ktory go otworzyl
            // (otwieramy w PreviewMouseDown). Zamykanie recznie: klik, Esc, F1, zmiana/ruch okna.
            StaysOpen = true,
            PopupAnimation = PopupAnimation.Fade
        };
        _popup.Child.PreviewMouseDown += (_, _) => ClosePopup();
        _popup.IsOpen = true;
    }

    private static void ClosePopup()
    {
        if (_popup != null) _popup.IsOpen = false;
        _popup = null;
    }

    /// <summary>Pierwszy element w gore drzewa z Topic albo tekstowym ToolTipem.</summary>
    private static (string? Text, FrameworkElement? Source) Resolve(DependencyObject start)
    {
        for (var d = start; d != null && d is not Window; d = Parent(d))
        {
            if (d is not FrameworkElement fe) continue;
            var topic = GetTopic(fe);
            if (!string.IsNullOrEmpty(topic))
            {
                var key = "Help_" + topic;
                var t = WpfLocalizer.T(key);
                if (t != key) return (t, fe);
            }
            var tip = fe.ToolTip switch
            {
                string s => s,
                ToolTip { Content: string s } => s,
                _ => null
            };
            if (!string.IsNullOrWhiteSpace(tip)) return (tip, fe);
        }
        return (null, null);
    }

    /// <summary>
    /// Tytul: tekst przycisku/checkboxa; dla pola w Gridzie formularza - etykieta z kolumny 0 tego samego wiersza.
    /// </summary>
    private static string? TitleFor(DependencyObject start)
    {
        for (var d = start; d != null && d is not Window; d = Parent(d))
        {
            if (d is ContentControl { Content: string s } && !string.IsNullOrWhiteSpace(s) && s != "...")
                return s.Trim();
            if (d is FrameworkElement fe && Parent(fe) is Grid g && Grid.GetColumn(fe) > 0)
            {
                var row = Grid.GetRow(fe);
                var label = g.Children.OfType<TextBlock>()
                    .FirstOrDefault(t => Grid.GetColumn(t) == 0 && Grid.GetRow(t) == row && !string.IsNullOrWhiteSpace(t.Text));
                if (label != null) return label.Text.TrimEnd(':', ' ');
            }
        }
        return null;
    }

    private static DependencyObject? Parent(DependencyObject d) =>
        d is Visual or System.Windows.Media.Media3D.Visual3D
            ? VisualTreeHelper.GetParent(d) ?? LogicalTreeHelper.GetParent(d)
            : LogicalTreeHelper.GetParent(d);

    private static Brush Res(string key) => (Brush)Application.Current.FindResource(key);
}
