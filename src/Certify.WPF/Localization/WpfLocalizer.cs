using System.Windows;
using Certify.Core.Localization;
using Certify.Core.Models;

namespace Certify.WPF;

/// <summary>
/// Laduje slownik stringow do zasobow aplikacji (DynamicResource w XAML).
/// Zmiana jezyka podmienia slownik na zywo.
/// </summary>
public static class WpfLocalizer
{
    private const string DictKey = "Strings";

    public static string Current { get; private set; } = "pl";

    /// <summary>Po podmianie slownika - dla tekstow skladanych w kodzie (np. tytul z wersja).</summary>
    public static event Action? LanguageChanged;

    public static void Apply(AppSettings settings)
    {
        var lang = UIStrings.ResolveLang(settings.Language,
            System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName);
        ApplyLang(lang);
    }

    public static FlowDirection FlowDirection => UIStrings.IsRightToLeft(Current) ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;

    private static bool _flowHandlerRegistered;

    public static void ApplyLang(string lang)
    {
        Current = UIStrings.IsSupported(lang) ? lang : "en";
        UIStrings.Lang = Current;
        var dict = new ResourceDictionary();
        foreach (var key in UIStrings.Keys)
            dict[key] = UIStrings.T(key);
        var app = Application.Current;
        if (app == null) return;
        for (var i = app.Resources.MergedDictionaries.Count - 1; i >= 0; i--)
        {
            if (app.Resources.MergedDictionaries[i].Contains(DictKey))
                app.Resources.MergedDictionaries.RemoveAt(i);
        }
        var holder = new ResourceDictionary();
        holder[DictKey] = true;
        holder.MergedDictionaries.Add(dict);
        app.Resources.MergedDictionaries.Add(holder);

        // ar/fa: uklad od prawej. Otwarte okna od razu, nowe przy Loaded.
        if (!_flowHandlerRegistered)
        {
            _flowHandlerRegistered = true;
            EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent,
                new RoutedEventHandler((s, _) => { if (s is Window w) w.FlowDirection = FlowDirection; }));
        }
        foreach (Window w in app.Windows) w.FlowDirection = FlowDirection;

        LanguageChanged?.Invoke();
    }

    public static string T(string key, params object?[] args) => UIStrings.T(key, args);
}
