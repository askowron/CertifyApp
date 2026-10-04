using System.Windows;
using Certify.Core.Services;

namespace Certify.WPF;

public partial class PasswordWindow : Window
{
    private readonly bool _confirm;
    public string Password { get; private set; } = string.Empty;

    /// <param name="prompt">Tekst instrukcji.</param>
    /// <param name="confirm">True = export (2x haslo), False = import (1x).</param>
    public PasswordWindow(string prompt, bool confirm)
    {
        InitializeComponent();
        PromptText.Text = prompt;
        _confirm = confirm;
        if (!confirm)
        {
            ConfirmLabel.Visibility = Visibility.Collapsed;
            ConfirmBox.Visibility = Visibility.Collapsed;
        }
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        var err = BackupEncryption.ValidatePassword(PassBox.Password);
        if (err != null) { MessageBox.Show(this, err); return; }
        if (_confirm && PassBox.Password != ConfirmBox.Password)
        {
            MessageBox.Show(this, WpfLocalizer.T("Pw_Err_Match"));
            return;
        }
        Password = PassBox.Password;
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
