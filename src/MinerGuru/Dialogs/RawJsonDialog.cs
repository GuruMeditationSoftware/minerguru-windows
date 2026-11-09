using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using MinerGuru.Controls;
using MinerGuru.Core.Models;
using Windows.ApplicationModel.DataTransfer;
using static MinerGuru.Core.Lang;

namespace MinerGuru.Dialogs;

/// <summary>Shows the raw <c>/api/system/info</c> response of a miner, with copy to clipboard.</summary>
public sealed partial class RawJsonDialog : ContentDialog
{
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public RawJsonDialog(MinerDevice miner)
    {
        Style = (Style)Application.Current.Resources["DefaultContentDialogStyle"];
        Resources["ContentDialogMaxWidth"] = 760.0;
        Title = L("API-Rohdaten (/api/system/info)", "Raw API data (/api/system/info)");
        CloseButtonText = L("Schließen", "Close");
        PrimaryButtonText = L("JSON kopieren", "Copy JSON");

        var json = Readable(miner.RawJson);
        var text = Palette.MonoText(json, 12, Palette.TextPrimary);
        text.IsTextSelectionEnabled = true;
        var scroll = new ScrollViewer
        {
            Height = 460,
            Padding = new Thickness(12),
            Content = text,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollMode = ScrollMode.Auto,
        };
        var box = new Border { Style = (Style)Application.Current.Resources["InsetBorderStyle"], Child = scroll };

        var content = new StackPanel { Spacing = 10, Width = 660 };
        content.Children.Add(Palette.MonoText($"http://{miner.IpAddress}/api/system/info", 12, Palette.TextSecondary));
        content.Children.Add(box);
        Content = content;

        PrimaryButtonClick += (_, args) =>
        {
            args.Cancel = true;
            var package = new DataPackage();
            package.SetText(json);
            Clipboard.SetContent(package);
            PrimaryButtonText = L("Kopiert", "Copied");
        };
    }

    /// <summary>Firmware sends the JSON in one line.</summary>
    private static string Readable(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "{}";
        try { return JsonNode.Parse(raw)?.ToJsonString(Indented) ?? raw; }
        catch (JsonException) { return raw; }
    }
}
