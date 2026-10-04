using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Certify.WPF;

/// <summary>
/// Kolorowa ikona przed tekstem przycisku: local:ButtonIcon.Kind="Delete".
/// Rodzaj -> glif Segoe Fluent Icons / MDL2 Assets + kolor z jednego katalogu (ta sama akcja
/// = ta sama ikona w calej aplikacji). Na PrimaryButton (gradient) ikona jest biala.
/// Pusty Content = sam przycisk-ikona (np. "..." -> folder).
/// Ustawia ContentTemplate zamiast podmieniac Content - Content z {DynamicResource} zmienia sie
/// przy przelaczeniu jezyka i ikona musi to przetrwac.
/// </summary>
public static class ButtonIcon
{
    // Trzy kolory wg znaczenia: akcent (domyslny), zielony = tworzenie/uruchomienie,
    // czerwony = usuwanie/zatrzymanie. Wiecej kolorow robilo sie pstrokato.
    private const string Accent = "#60A5FA", Green = "#4ADE80", Red = "#F87171";

    private static readonly Dictionary<string, (string Glyph, string Color)> Catalog = new()
    {
        // ogolne
        ["Add"] = ("\uE710", Green),
        ["Edit"] = ("\uE70F", Accent),
        ["Delete"] = ("\uE74D", Red),
        ["Save"] = ("\uE74E", Accent),
        ["Ok"] = ("\uE8FB", Green),
        ["Cancel"] = ("\uE711", Accent),
        ["Close"] = ("\uE8BB", Accent),
        ["Browse"] = ("\uE838", Accent),
        ["Refresh"] = ("\uE72C", Accent),
        ["Help"] = ("\uE897", Accent),
        ["Settings"] = ("\uE713", Accent),
        ["Info"] = ("\uE946", Accent),
        ["Prev"] = ("\uE76B", Accent),
        ["Next"] = ("\uE76C", Accent),
        ["Today"] = ("\uE8D1", Accent),
        ["Send"] = ("\uE724", Accent),
        // certyfikaty
        ["Request"] = ("\uEB95", Green),
        ["RenewAll"] = ("\uE895", Accent),
        ["Test"] = ("\uE9D9", Accent),
        ["Deploy"] = ("\uE898", Accent),
        ["Export"] = ("\uEDE1", Accent),
        ["Import"] = ("\uE8B5", Accent),
        ["Preview"] = ("\uE890", Accent),
        ["Backup"] = ("\uE78C", Accent),
        ["History"] = ("\uE81C", Accent),
        ["Calendar"] = ("\uE787", Accent),
        ["Folder"] = ("\uE838", Accent),
        ["Log"] = ("\uE8A5", Accent),
        ["Clear"] = ("\uE894", Accent),
        ["Iis"] = ("\uE774", Accent),
        // usluga / harmonogram
        ["Schedule"] = ("\uE823", Accent),
        ["Service"] = ("\uE912", Accent),
        ["Install"] = ("\uE896", Green),
        ["Update"] = ("\uE895", Accent),
        ["Start"] = ("\uE768", Green),
        ["Stop"] = ("\uE71A", Red),
        ["Pause"] = ("\uE769", Accent),
        ["Enable"] = ("\uE73E", Green),
    };

    public static readonly DependencyProperty KindProperty = DependencyProperty.RegisterAttached(
        "Kind", typeof(string), typeof(ButtonIcon), new PropertyMetadata(null, OnKindChanged));
    public static readonly DependencyProperty GlyphProperty = DependencyProperty.RegisterAttached(
        "Glyph", typeof(string), typeof(ButtonIcon), new PropertyMetadata(null));
    public static readonly DependencyProperty IconBrushProperty = DependencyProperty.RegisterAttached(
        "IconBrush", typeof(Brush), typeof(ButtonIcon), new PropertyMetadata(Brushes.White));

    public static string? GetKind(DependencyObject d) => (string?)d.GetValue(KindProperty);
    public static void SetKind(DependencyObject d, string? value) => d.SetValue(KindProperty, value);
    public static string? GetGlyph(DependencyObject d) => (string?)d.GetValue(GlyphProperty);
    public static Brush GetIconBrush(DependencyObject d) => (Brush)d.GetValue(IconBrushProperty);

    private static void OnKindChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not ContentControl c) return;
        if (e.NewValue is not string kind || !Catalog.TryGetValue(kind, out var icon))
        {
            c.ContentTemplate = null;
            return;
        }
        c.SetValue(GlyphProperty, icon.Glyph);
        c.SetValue(IconBrushProperty, BrushFor(c, icon.Color));
        c.ContentTemplate = (DataTemplate)Application.Current.FindResource("IconButtonContent");
        // Style z XAML moze byc ustawiony po Kind - kolor dla PrimaryButton poprawiamy po zaladowaniu.
        c.Loaded -= OnLoaded;
        c.Loaded += OnLoaded;
    }

    private static void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is ContentControl c && GetKind(c) is { } kind && Catalog.TryGetValue(kind, out var icon))
            c.SetValue(IconBrushProperty, BrushFor(c, icon.Color));
    }

    private static Brush BrushFor(ContentControl c, string color)
    {
        if (c.Style != null && ReferenceEquals(c.Style, Application.Current.TryFindResource("PrimaryButton")))
            return Brushes.White;
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));
        brush.Freeze();
        return brush;
    }
}
