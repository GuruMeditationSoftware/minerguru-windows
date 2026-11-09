using Microsoft.UI.Xaml.Controls;
using MinerGuru.Controls;
using MinerGuru.Core;
using MinerGuru.Core.Models;
using MinerGuru.Core.Services;

namespace MinerGuru.Dialogs;

public sealed partial class PoolConfigDialog : ContentDialog
{
    private readonly PoolConfig _config;
    /// <summary>Set after a test; <c>null</c> latency then means the pool did not answer.</summary>
    private bool _tested;
    private double? _testedLatencyMs;

    /// <param name="config">Edited in place only when the dialog is confirmed; <see cref="Result"/> holds the saved pool.</param>
    public PoolConfigDialog(PoolConfig config, bool isNew)
    {
        InitializeComponent();
        _config = config;
        TitleText.Text = isNew ? Strings.NewPoolConfiguration : Strings.EditPoolConfiguration;
        PrimaryButtonText = isNew ? Strings.Create : Strings.Save;
        NameBox.Text = config.Name;
        HostBox.Text = config.Host;
        PortBox.Value = config.Port;
        PasswordBox.Text = config.Password;
        UserBox.Text = config.User;

        foreach (var preset in PoolPreset.CommonPools)
        {
            var item = new MenuFlyoutItem { Text = $"{preset.Name} ({preset.Host}:{preset.Port})" };
            item.Click += (_, _) =>
            {
                HostBox.Text = preset.Host;
                PortBox.Value = preset.Port;
                if (NameBox.Text.Trim().Length == 0) NameBox.Text = preset.Name;
            };
            TemplateMenu.Items.Add(item);
        }
        Update();
    }

    public PoolConfig? Result { get; private set; }

    /// <summary>The entered pool, trimmed; an empty name becomes the suggested one.</summary>
    private PoolConfig Cleaned()
    {
        var cleaned = _config.Copy();
        cleaned.Host = StratumHost.Clean(HostBox.Text);
        cleaned.Port = double.IsNaN(PortBox.Value) ? 0 : (int)PortBox.Value;
        cleaned.User = UserBox.Text.Trim();
        cleaned.Password = PasswordBox.Text.Length == 0 ? "x" : PasswordBox.Text;
        cleaned.Name = NameBox.Text.Trim();
        if (cleaned.Name.Length == 0) cleaned.Name = PoolConfig.SuggestedName(cleaned.Host, cleaned.Port, cleaned.User);
        return cleaned;
    }

    private void Update()
    {
        var cleaned = Cleaned();
        IsPrimaryButtonEnabled = cleaned.IsValid;
        TestButton.IsEnabled = cleaned.IsValid && !TestRing.IsActive;
        BadgeRow.Children.Clear();
        if (cleaned.Host.Length > 0)
            BadgeRow.Children.Add(Badges.Coin(MinedCoinDetector.Detect(cleaned.Host, cleaned.User), showFullName: true));
        if (_tested && !TestRing.IsActive)
            BadgeRow.Children.Add(Badges.Latency(_testedLatencyMs));
        var warning = MinedCoinDetector.MismatchWarning(cleaned.Host, cleaned.User);
        CoinWarningText.Text = warning ?? "";
        CoinWarningText.Visibility = warning == null ? Microsoft.UI.Xaml.Visibility.Collapsed : Microsoft.UI.Xaml.Visibility.Visible;
    }

    private void Field_Changed(object sender, TextChangedEventArgs e) => Update();

    private void PortBox_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args) => Update();

    private async void Test_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        var target = Cleaned();
        TestRing.IsActive = true;
        TestRing.Visibility = Microsoft.UI.Xaml.Visibility.Visible;
        Update();
        _testedLatencyMs = await PoolLibrary.MeasureLatencyMsAsync(target.Host, target.Port);
        _tested = true;
        TestRing.IsActive = false;
        TestRing.Visibility = Microsoft.UI.Xaml.Visibility.Collapsed;
        Update();
    }

    private void Dialog_PrimaryButtonClick(ContentDialog sender, ContentDialogButtonClickEventArgs args) => Result = Cleaned();
}
