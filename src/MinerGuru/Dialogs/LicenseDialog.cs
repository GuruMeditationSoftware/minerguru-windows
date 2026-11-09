using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using MinerGuru.Controls;
using static MinerGuru.Core.Lang;

namespace MinerGuru.Dialogs;

/// <summary>The app's license and the third-party notices, as installed next to the exe.</summary>
public sealed partial class LicenseDialog : ContentDialog
{
    public LicenseDialog()
    {
        Style = (Style)Application.Current.Resources["DefaultContentDialogStyle"];
        Title = L("Lizenz", "License");
        CloseButtonText = L("Schließen", "Close");
        var form = new StackPanel { Width = 500, Spacing = 12 };
        var hint = Palette.UiText(L(
            "Open Source unter der MIT-Lizenz und kostenlos nutzbar. Nutzung und Änderungen an der Hardware auf eigenes Risiko.",
            "Open source under the MIT license and free to use. Use and changes to your hardware are at your own risk."), 13, Palette.TextSecondary);
        hint.TextWrapping = TextWrapping.Wrap;
        form.Children.Add(hint);
        var text = string.Join("\n\n\n", new[] { "LICENSE.txt", "THIRD-PARTY-NOTICES.txt" }
            .Select(name => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Assets", name)).Trim()));
        form.Children.Add(new ScrollViewer
        {
            Height = 400,
            Content = new TextBlock
            {
                Text = text,
                FontFamily = new FontFamily("Consolas"),
                FontSize = 11,
                TextWrapping = TextWrapping.Wrap,
                IsTextSelectionEnabled = true,
                Padding = new Thickness(0, 0, 16, 0),
            },
        });
        Content = form;
    }
}
