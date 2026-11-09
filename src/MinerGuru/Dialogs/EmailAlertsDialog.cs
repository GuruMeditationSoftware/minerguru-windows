using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using MinerGuru.Controls;
using MinerGuru.Core.Services;

namespace MinerGuru.Dialogs;

/// <summary>Mail server and addresses for alerts by mail. The password is kept encrypted for the Windows user.</summary>
public sealed partial class EmailAlertsDialog : ContentDialog
{
    private static readonly SmtpSecurity[] Securities = Enum.GetValues<SmtpSecurity>();

    private readonly ToggleSwitch _enabled = new() { OnContent = "", OffContent = "", MinWidth = 0 };
    private readonly TextBox _host = new() { PlaceholderText = "smtp.example.com" };
    private readonly NumberBox _port = new() { Minimum = 1, Maximum = 65535, Width = 110, HorizontalAlignment = HorizontalAlignment.Left };
    private readonly ComboBox _security = new() { MinWidth = 140 };
    private readonly TextBox _username = new();
    private readonly PasswordBox _password = new();
    private readonly TextBox _sender = new() { PlaceholderText = "miner@example.com" };
    private readonly TextBox _recipients = new() { PlaceholderText = "ich@example.com, du@example.com" };
    private readonly TextBlock _warning = PowerForm.Note(Strings.WithoutEncryptionPasswordAndMail);
    private readonly TextBlock _status = PowerForm.Note("");
    private readonly Button _test = new() { Content = Strings.TestMail };
    private bool _passwordTouched;

    public EmailAlertsDialog()
    {
        var mail = App.Store.Email;
        var settings = mail.Settings;
        Style = (Style)Application.Current.Resources["DefaultContentDialogStyle"];
        Resources["ContentDialogMaxWidth"] = 600.0;
        PrimaryButtonText = Strings.SaveEntry;
        CloseButtonText = Strings.Cancel;
        DefaultButton = ContentDialogButton.Primary;

        _enabled.IsOn = settings.IsEnabled;
        _host.Text = settings.Host;
        _port.Value = settings.Port;
        AutomationProperties.SetName(_security, Strings.Encryption);
        foreach (var security in Securities) _security.Items.Add(security.Title());
        _security.SelectedIndex = Array.IndexOf(Securities, settings.Security);
        _username.Text = settings.Username;
        _sender.Text = settings.Sender;
        _recipients.Text = settings.Recipients;
        _password.PlaceholderText = mail.HasPassword ? Strings.Saved : "";
        _password.PasswordChanged += (_, _) => _passwordTouched = true;
        _security.SelectionChanged += (_, _) =>
        {
            _port.Value = Securities[_security.SelectedIndex].DefaultPort();
            Sync();
        };
        foreach (var box in new[] { _host, _sender, _recipients }) box.TextChanged += (_, _) => Sync();
        _port.ValueChanged += (_, _) => Sync();
        _test.Click += async (_, _) => await TestAsync();
        _warning.Foreground = Palette.Warning;

        var form = new StackPanel { Width = 520, Spacing = 12 };
        form.Children.Add(PageParts.Title(Strings.EmailAlerts));
        form.Children.Add(PowerForm.Row(Strings.Active, _enabled));
        form.Children.Add(PowerForm.Row("Server", _host));
        form.Children.Add(PowerForm.Row(Strings.Security, _security));
        form.Children.Add(PowerForm.Row("Port", _port));
        form.Children.Add(_warning);
        form.Children.Add(PowerForm.Row(Strings.Username, _username));
        form.Children.Add(PowerForm.Row(Strings.Password, _password));
        form.Children.Add(PowerForm.Row(Strings.Sender, _sender));
        form.Children.Add(PowerForm.Row(Strings.Recipients, _recipients));
        form.Children.Add(PowerForm.Note(Strings.SeparateSeveralAddressesWithCommas));
        form.Children.Add(_test);
        form.Children.Add(_status);
        Content = new ScrollViewer { Content = form, MaxHeight = 560 };
        Sync();

        PrimaryButtonClick += (_, args) =>
        {
            if (mail.Save(Form(), NewPassword)) return;
            args.Cancel = true;
            _status.Foreground = Palette.Bad;
            _status.Text = Strings.ThePasswordCouldNotBe;
        };
    }

    /// <summary><c>null</c> keeps the stored password, an emptied box removes it.</summary>
    private string? NewPassword => _passwordTouched ? _password.Password : null;

    private EmailSettings Form() => new()
    {
        IsEnabled = _enabled.IsOn,
        Host = _host.Text.Trim(),
        Port = double.IsNaN(_port.Value) ? 0 : (int)_port.Value,
        Security = Securities[Math.Max(0, _security.SelectedIndex)],
        Username = _username.Text.Trim(),
        Sender = _sender.Text.Trim(),
        Recipients = _recipients.Text,
    };

    private void Sync()
    {
        _warning.Visibility = Securities[Math.Max(0, _security.SelectedIndex)] == SmtpSecurity.None ? Visibility.Visible : Visibility.Collapsed;
        _test.IsEnabled = Form().IsComplete;
    }

    private async Task TestAsync()
    {
        _test.IsEnabled = false;
        _status.Foreground = Palette.TextSecondary;
        _status.Text = Strings.Sending;
        try
        {
            await App.Store.Email.SendTestAsync(Form(), NewPassword);
            _status.Text = Strings.TestMailSent;
        }
        catch (Exception e) when (e is IOException or System.Net.Sockets.SocketException or OperationCanceledException or System.Security.Authentication.AuthenticationException)
        {
            _status.Foreground = Palette.Bad;
            _status.Text = e is OperationCanceledException or System.Net.Sockets.SocketException ? Strings.MailServerNotReachable : e.Message;
        }
        _test.IsEnabled = true;
    }
}
