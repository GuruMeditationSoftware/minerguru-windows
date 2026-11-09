using System.Runtime.InteropServices;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;
using MinerGuru.Controls;
using MinerGuru.Core;
using MinerGuru.Core.Services;
using static MinerGuru.Core.Lang;

namespace MinerGuru.Dialogs;

/// <summary>
/// Shows old and new values before a write by hand. A small window of its own, not a ContentDialog:
/// writes often start from an open dialog, and a window holds only one of those at a time.
/// </summary>
public sealed partial class ConfirmWriteWindow : Window
{
    private readonly TaskCompletionSource<bool> _answer = new();

    private ConfirmWriteWindow(string minerName, IReadOnlyList<ChangeField> fields, bool restarts, ElementTheme theme)
    {
        Title = "MinerGuru";
        SystemBackdrop = new MicaBackdrop();
        var title = new TextBlock
        {
            Text = L($"An {minerName} übertragen?", $"Send to {minerName}?"),
            FontSize = 18, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap,
        };
        var lines = new StackPanel { Spacing = 4 };
        if (fields.Count == 0) lines.Children.Add(new TextBlock { Text = L("Es ändert sich kein Wert.", "No value changes.") });
        foreach (var field in fields)
            lines.Children.Add(new TextBlock { Text = Privacy.FreeText(field.Summary), TextWrapping = TextWrapping.Wrap });
        if (restarts) lines.Children.Add(new TextBlock { Text = L("Danach startet der Miner neu.", "The miner restarts afterwards.") });
        var scroller = new ScrollViewer { Content = lines };
        var cancel = PageParts.TextButton("", L("Abbrechen", "Cancel"), () => Finish(false));
        var confirm = PageParts.TextButton("", L("Übertragen", "Send"), () => Finish(true), accent: true);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(cancel);
        buttons.Children.Add(confirm);
        var body = new Grid { RowSpacing = 14, Padding = new Thickness(24), RequestedTheme = theme };
        body.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        body.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        body.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Grid.SetRow(scroller, 1);
        Grid.SetRow(buttons, 2);
        body.Children.Add(title);
        body.Children.Add(scroller);
        body.Children.Add(buttons);
        Content = body;

        AppWindow.TitleBar.PreferredTheme = theme == ElementTheme.Light ? TitleBarTheme.Light : TitleBarTheme.Dark;
        var presenter = OverlappedPresenter.CreateForDialog();
        presenter.IsAlwaysOnTop = true;
        AppWindow.SetPresenter(presenter);
        var scale = GetDpiForWindow(WinRT.Interop.WindowNative.GetWindowHandle(this)) / 96.0;
        var size = new SizeInt32((int)(480 * scale), (int)(Math.Min(560, 190 + 26 * (fields.Count + 1)) * scale));
        var area = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
        AppWindow.MoveAndResize(new RectInt32(area.X + (area.Width - size.Width) / 2, area.Y + (area.Height - size.Height) / 2, size.Width, size.Height));
        Closed += (_, _) => _answer.TrySetResult(false);
    }

    public static Task<bool> AskAsync(string minerName, IReadOnlyList<ChangeField> fields, bool restarts, ElementTheme theme)
    {
        var window = new ConfirmWriteWindow(minerName, fields, restarts, theme);
        window.Activate();
        return window._answer.Task;
    }

    private void Finish(bool answer)
    {
        _answer.TrySetResult(answer);
        Close();
    }

    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(nint window);
}
