using System.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using MinerGuru.Controls;
using MinerGuru.Core;
using MinerGuru.Core.Services;

namespace MinerGuru.Dialogs;

/// <summary>Every write to a miner with old and new values.</summary>
internal static class ChangeLogDialog
{
    public static async Task ShowForAsync(XamlRoot root)
    {
        var entries = ChangeLog.Shared.Entries();
        var list = new StackPanel { Spacing = 12 };
        if (entries.Count == 0) list.Children.Add(Palette.UiText(Strings.NoChangesYet, 13, Palette.TextSecondary));
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
            Title = Strings.ChangeLog,
            Content = new ScrollViewer { Content = list, MaxHeight = 420 },
            PrimaryButtonText = entries.Count == 0 ? "" : Strings.Export,
            CloseButtonText = Strings.Close,
        };
        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
            await PageParts.SaveFileAsync("minerguru-changes.txt", Strings.TextFile, ".txt", Encoding.UTF8.GetBytes(ChangeLog.Shared.ExportText()));
    }
}
