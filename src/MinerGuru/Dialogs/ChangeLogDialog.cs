using System.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using MinerGuru.Controls;
using MinerGuru.Core;
using MinerGuru.Core.Services;
using static MinerGuru.Core.Lang;

namespace MinerGuru.Dialogs;

/// <summary>Every write to a miner with old and new values.</summary>
internal static class ChangeLogDialog
{
    public static async Task ShowForAsync(XamlRoot root)
    {
        var entries = ChangeLog.Shared.Entries();
        var list = new StackPanel { Spacing = 12 };
        if (entries.Count == 0) list.Children.Add(Palette.UiText(L("Noch keine Änderungen.", "No changes yet."), 13, Palette.TextSecondary));
        foreach (var entry in entries)
        {
            var block = new StackPanel { Spacing = 2 };
            block.Children.Add(Palette.UiText(
                $"{entry.Date:g} · {Privacy.Ip(entry.Host)} · {ChangeLog.ActionTitle(entry.Action)} · {ChangeLog.SourceTitle(entry.Source)}", 13, Palette.TextPrimary));
            foreach (var field in entry.Fields)
                block.Children.Add(Palette.MonoText(Privacy.FreeText(field.Summary), 11, Palette.TextSecondary));
            if (entry.Failure != null) block.Children.Add(Palette.UiText(entry.Failure, 12, Palette.Bad));
            list.Children.Add(block);
        }
        var dialog = new ContentDialog
        {
            XamlRoot = root,
            Style = (Style)Application.Current.Resources["DefaultContentDialogStyle"],
            Title = L("Änderungsprotokoll", "Change log"),
            Content = new ScrollViewer { Content = list, MaxHeight = 420 },
            PrimaryButtonText = entries.Count == 0 ? "" : L("Exportieren …", "Export …"),
            CloseButtonText = L("Schließen", "Close"),
        };
        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
            await PageParts.SaveFileAsync("minerguru-changes.txt", L("Textdatei", "Text file"), ".txt", Encoding.UTF8.GetBytes(ChangeLog.Shared.ExportText()));
    }
}
