using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace MinerGuru.Controls;

/// <summary>Schedules skip miners that have no valid session: this names them and lets the user enter a code.</summary>
internal static class OtpWaitingNotice
{
    /// <summary>Changes when the notice has to be built anew.</summary>
    public static string Key => string.Join(',', App.Store.MinersAwaitingOtp.Select(m => $"{m.Id} {m.DisplayName}"));

    /// <summary>The notice, or <c>null</c> when no miner waits for a code.</summary>
    public static StackPanel? Build()
    {
        var waiting = App.Store.MinersAwaitingOtp;
        if (waiting.Count == 0) return null;
        var panel = new StackPanel { Spacing = 6 };
        var note = Palette.UiText(Strings.SchedulesOnlyRunOnThese, 12, Palette.TextSecondary);
        note.TextWrapping = TextWrapping.Wrap;
        panel.Children.Add(note);
        foreach (var miner in waiting)
        {
            var row = new Grid();
            var name = Palette.UiText(miner.DisplayName, 13, Palette.TextPrimary);
            name.VerticalAlignment = VerticalAlignment.Center;
            row.Children.Add(name);
            var button = PageParts.TextButton("\uE72E", Strings.EnterCode, () => _ = App.Store.EnterOtpCodeAsync(miner));
            button.HorizontalAlignment = HorizontalAlignment.Right;
            row.Children.Add(button);
            panel.Children.Add(row);
        }
        return panel;
    }
}
