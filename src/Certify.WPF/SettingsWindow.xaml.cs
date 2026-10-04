using System.Windows;
using Certify.Core.Localization;
using Certify.Core.Models;
using Certify.Core.Services;

namespace Certify.WPF;

public partial class SettingsWindow : Window
{
    // Edycja na kopii: Anuluj (takze po "Testuj") nie zmienia ustawien uzywanych
    // przez dzialajaca aplikacje. Wywolujacy przepisuje Result po Zapisz.
    private readonly AppSettings _settings;

    public AppSettings Result => _settings;

    public SettingsWindow(AppSettings settings)
    {
        InitializeComponent();
        _settings = settings.Clone();
        EnabledCheck.IsChecked = _settings.EnableEmailNotifications;
        HostBox.Text = _settings.SmtpHost;
        PortBox.Text = _settings.SmtpPort.ToString();
        SslCheck.IsChecked = _settings.SmtpUseSsl;
        UserBox.Text = _settings.SmtpUsername;
        PassBox.Password = _settings.SmtpPassword;
        FromBox.Text = _settings.EmailFrom;
        ToBox.Text = _settings.EmailTo;
        WarnDaysBox.Text = _settings.ExpiryWarningDays.ToString();
        var langs = new List<KeyValuePair<string, string>> { new("Auto", WpfLocalizer.T("Settings_Lang_Auto")) };
        langs.AddRange(UIStrings.Languages.Select(l => new KeyValuePair<string, string>(l.Code, l.Name)));
        LangBox.ItemsSource = langs;
        LangBox.DisplayMemberPath = "Value";
        LangBox.SelectedValuePath = "Key";
        LangBox.SelectedValue = UIStrings.IsSupported(_settings.Language) ? _settings.Language : "Auto";
    }

    private void ReadForm()
    {
        _settings.EnableEmailNotifications = EnabledCheck.IsChecked == true;
        _settings.SmtpHost = HostBox.Text.Trim();
        if (int.TryParse(PortBox.Text, out var p)) _settings.SmtpPort = p;
        _settings.SmtpUseSsl = SslCheck.IsChecked == true;
        _settings.SmtpUsername = UserBox.Text.Trim();
        _settings.SmtpPassword = PassBox.Password;
        _settings.EmailFrom = FromBox.Text.Trim();
        _settings.EmailTo = ToBox.Text.Trim();
        if (int.TryParse(WarnDaysBox.Text, out var w) && w >= 0) _settings.ExpiryWarningDays = w;
        _settings.Language = LangBox.SelectedValue is string code && UIStrings.IsSupported(code) ? code : "Auto";
    }

    private async void Test_Click(object sender, RoutedEventArgs e)
    {
        ReadForm();
        TestResult.Text = WpfLocalizer.T("Settings_Sending");
        var notifier = new EmailNotifier(_settings);
        var (ok, msg) = await notifier.SendAsync(
            WpfLocalizer.T("Settings_TestSubject"),
            WpfLocalizer.T("Settings_TestBody", DateTime.Now.ToString("yyyy-MM-dd HH:mm")));
        TestResult.Text = ok ? WpfLocalizer.T("Settings_TestOk") : WpfLocalizer.T("Settings_TestErr", msg);
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        ReadForm();
        if (_settings.EnableEmailNotifications)
        {
            var err = new EmailNotifier(_settings).ValidateConfig();
            if (err != null) { MessageBox.Show(this, err, WpfLocalizer.T("Win_Settings_Title"), MessageBoxButton.OK, MessageBoxImage.Warning); return; }
        }
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
