using System.Reflection;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Certify.WPF;

/// <summary>Wersja i ikona aplikacji (jedno zrodlo dla tytulu i okien).</summary>
public static class AppInfo
{
    /// <summary>AssemblyVersion z Certify.WPF.csproj (np. 26.10.4.2).</summary>
    public static string Version { get; } =
        typeof(AppInfo).Assembly.GetName().Version?.ToString() ?? "?";

    private static ImageSource? _icon;

    public static ImageSource? Icon
    {
        get
        {
            if (_icon != null) return _icon;
            try
            {
                var frame = BitmapFrame.Create(new Uri("pack://application:,,,/Assets/certify.ico", UriKind.Absolute));
                frame.Freeze();
                _icon = frame;
            }
            catch { }
            return _icon;
        }
    }

    /// <summary>
    /// Ikona na pasku tytulowym kazdego okna (takze dialogow), bez dopisywania Icon= w kazdym XAML.
    /// </summary>
    public static void RegisterWindowIcon() =>
        EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent, new RoutedEventHandler((s, _) =>
        {
            if (s is Window w && w.Icon == null && Icon != null) w.Icon = Icon;
        }));
}
