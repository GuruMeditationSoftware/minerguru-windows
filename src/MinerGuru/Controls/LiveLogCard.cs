using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;
using MinerGuru.Core;
using MinerGuru.Core.Services;

namespace MinerGuru.Controls;

/// <summary>The miner's live log: start, stop, clear, search. Runs only while the user started it.</summary>
public sealed partial class LiveLogCard : UserControl
{
    private readonly TextBlock _lines = new() { FontSize = 11, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true };
    private readonly ScrollViewer _scroll = new() { Height = 300, Padding = new Thickness(12) };
    private readonly Button _toggle = new();
    private readonly Button _clear = new() { Content = Strings.Clear };
    private readonly TextBox _filter = new() { PlaceholderText = Strings.Search, Width = 180 };
    private readonly TextBlock _status = new() { FontSize = 11 };
    private MinerLogStream? _stream;

    public LiveLogCard()
    {
        _lines.FontFamily = Palette.Mono;
        _scroll.Content = _lines;
        var style = (Style)Application.Current.Resources["SmallButtonStyle"];
        _toggle.Style = style;
        _clear.Style = style;
        _toggle.Click += (_, _) =>
        {
            if (_stream == null) return;
            if (_stream.IsLive) _stream.StopLive();
            else _stream.StartLive();
        };
        _clear.Click += (_, _) => _stream?.Clear();
        _filter.TextChanged += (_, _) => Render();

        var title = Palette.UiText("Live-Log", 15, Palette.TextPrimary);
        title.FontWeight = FontWeights.Medium;
        title.VerticalAlignment = VerticalAlignment.Center;
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
        actions.Children.Add(_filter);
        actions.Children.Add(_clear);
        actions.Children.Add(_toggle);
        var header = new Grid();
        header.Children.Add(title);
        header.Children.Add(actions);

        var body = new StackPanel { Spacing = 12 };
        body.Children.Add(header);
        body.Children.Add(new Border { Child = _scroll, Style = (Style)Application.Current.Resources["InsetBorderStyle"] });
        body.Children.Add(_status);
        Content = new Border { Child = body, Padding = new Thickness(24), CornerRadius = new CornerRadius(16), Style = (Style)Application.Current.Resources["CardBorderStyle"] };
        ActualThemeChanged += (_, _) => Render();
    }

    public void Update(MinerLogStream stream)
    {
        _stream = stream;
        Render();
    }

    private void Render()
    {
        if (_stream == null) return;
        _toggle.Content = _stream.IsLive ? Strings.Stop : Strings.StartLiveLog;
        _status.Foreground = _stream.Error != null ? Palette.Bad : Palette.TextTertiary;
        _status.Text = _stream.Error ?? (_stream.IsLive ? Strings.Connected : Strings.NotConnected);

        var filter = _filter.Text.Trim();
        var shown = _stream.Lines.Where(l => filter.Length == 0 || l.Text.Contains(filter, StringComparison.OrdinalIgnoreCase)).TakeLast(200).ToList();
        var follow = _scroll.ScrollableHeight - _scroll.VerticalOffset < 40;
        _lines.Inlines.Clear();
        if (shown.Count == 0)
        {
            _lines.Inlines.Add(new Run { Text = _stream.Lines.Count == 0 ? Strings.NoLinesYetStartThe : Strings.NoMatches, Foreground = Palette.TextTertiary });
            return;
        }
        foreach (var line in shown)
        {
            _lines.Inlines.Add(new Run
            {
                Text = $"{line.Time:HH:mm:ss}  {Privacy.FreeText(line.Text)}\n",
                Foreground = line.Level switch { LogLevel.Error => Palette.Bad, LogLevel.Warning => Palette.Warning, LogLevel.Info => Palette.TextPrimary, _ => Palette.TextSecondary },
            });
        }
        if (follow) DispatcherQueue.TryEnqueue(() => _scroll.ChangeView(null, double.MaxValue, null, true));
    }
}
