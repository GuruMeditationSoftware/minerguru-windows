using Microsoft.UI.Xaml.Controls;
using MinerGuru.Core.Models;
using MinerGuru.Core.Services;
using static MinerGuru.Core.Lang;

namespace MinerGuru.Dialogs;

public sealed partial class AddMinerDialog : ContentDialog
{
    /// <summary>Set to the address that failed, so a second click adds it anyway.</summary>
    private string? _failedAddress;

    public AddMinerDialog()
    {
        InitializeComponent();
        IsPrimaryButtonEnabled = false;
    }

    public MinerDevice? AddedMiner { get; private set; }

    private void AddressBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        IsPrimaryButtonEnabled = AddressBox.Text.Trim().Length > 0;
        if (_failedAddress == null) return;
        _failedAddress = null;
        ResultBar.IsOpen = false;
        PrimaryButtonText = L("Hinzufügen", "Add");
    }

    private async void Dialog_PrimaryButtonClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        var address = MinerApiClient.NormalizeHost(AddressBox.Text);
        var name = NameBox.Text;

        if (_failedAddress == address)
        {
            AddedMiner = App.Store.AddMiner(address, name);
            return;
        }
        if (App.Store.IsAlreadyAdded(address) && string.IsNullOrWhiteSpace(name))
        {
            args.Cancel = true;
            ShowResult(InfoBarSeverity.Informational, L("Diesen Miner gibt es schon in MinerGuru.", "This miner is already in MinerGuru."));
            return;
        }

        // Keep the dialog open while the connection is checked.
        var deferral = args.GetDeferral();
        Progress.Visibility = Microsoft.UI.Xaml.Visibility.Visible;
        IsPrimaryButtonEnabled = false;
        try
        {
            var (info, raw) = await MinerApiClient.Shared.FetchSystemInfoAsync(address);
            AddedMiner = App.Store.AddMiner(address, name, info, raw);
        }
        catch (MinerApiException e)
        {
            args.Cancel = true;
            _failedAddress = address;
            ShowResult(InfoBarSeverity.Warning, L($"Keine Antwort von {address}: {e.Message}", $"No answer from {address}: {e.Message}"));
            PrimaryButtonText = L("Trotzdem hinzufügen", "Add anyway");
        }
        finally
        {
            Progress.Visibility = Microsoft.UI.Xaml.Visibility.Collapsed;
            IsPrimaryButtonEnabled = true;
            deferral.Complete();
        }
    }

    private void ShowResult(InfoBarSeverity severity, string message)
    {
        ResultBar.Severity = severity;
        ResultBar.Message = message;
        ResultBar.IsOpen = true;
    }
}
