using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using MinerGuru.Core;
using MinerGuru.Core.Models;
using MinerGuru.Core.Services;

namespace MinerGuru.Controls;

/// <summary>
/// Difficulty of every ASIC result over time on a log scale, with the pool difficulty as reference line. Results
/// above the line were submitted as shares.
/// </summary>
public sealed partial class ShareChart : UserControl
{
    private readonly Canvas _canvas = new() { Height = 190 };
    private readonly TextBlock _best = new();
    private readonly TextBlock _source = new();
    private readonly TextBlock _empty = new() { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
    private IReadOnlyList<ShareSample> _samples = [];
    private bool _live, _sampling;

    public ShareChart()
    {
        var title = Palette.UiText(Strings.DifficultyPerShare, 15, Palette.TextPrimary);
        title.FontWeight = Microsoft.UI.Text.FontWeights.Medium;
        var header = new Grid();
        header.Children.Add(title);
        _best.HorizontalAlignment = HorizontalAlignment.Right;
        _best.VerticalAlignment = VerticalAlignment.Center;
        header.Children.Add(_best);

        var plot = new Grid();
        plot.Children.Add(_canvas);
        plot.Children.Add(_empty);

        var body = new StackPanel { Spacing = 10 };
        body.Children.Add(header);
        body.Children.Add(plot);
        body.Children.Add(_source);
        Content = new Border { Child = body, Padding = new Thickness(24), CornerRadius = new CornerRadius(16), Style = (Style)Application.Current.Resources["CardBorderStyle"] };
        SizeChanged += (_, _) => Redraw();
        ActualThemeChanged += (_, _) => Redraw();
    }

    public void Update(MinerLogStream stream)
    {
        _samples = stream.Samples.ToList();
        _live = stream.IsLive;
        _sampling = stream.IsSampling;
        Redraw();
    }

    private void Redraw()
    {
        _canvas.Children.Clear();
        _best.FontFamily = Palette.Mono;
        _best.FontSize = 11;
        _best.Foreground = Palette.TextSecondary;
        _source.FontFamily = Palette.Mono;
        _source.FontSize = 11;
        _source.Foreground = Palette.TextTertiary;
        _empty.FontSize = 12;
        _empty.Foreground = Palette.TextTertiary;

        var submitted = _samples.Count(s => s.IsSubmitted);
        _source.Text = (_live ? Strings.LiveLogRunning : Strings.SampledOnceAMinute)
            + " · " + string.Format(Strings.ResultsAbovePoolDifficulty, _samples.Count, submitted);
        _best.Text = _samples.Count > 0 ? Strings.Best + MinerFormatting.FormatDifficulty(_samples.Max(s => s.Difficulty)) : "";
        _empty.Text = _samples.Count > 0 ? "" : _sampling ? Strings.CollectingShares : Strings.NoSharesYet;
        var width = ActualWidth - 48 - 70;
        if (_samples.Count == 0 || width < 40) return;

        const double height = 190;
        var low = Math.Log10(Math.Max(1, _samples.Min(s => Math.Min(s.Difficulty, s.PoolDifficulty ?? s.Difficulty))));
        var high = Math.Log10(Math.Max(10, _samples.Max(s => s.Difficulty)));
        if (high - low < 1) high = low + 1;
        var start = _samples[0].Date;
        var span = Math.Max(1, (_samples[^1].Date - start).TotalSeconds);
        double X(DateTime t) => (t - start).TotalSeconds / span * width;
        double Y(double v) => height - 6 - (Math.Log10(Math.Max(1, v)) - low) / (high - low) * (height - 12);

        foreach (var exponent in Enumerable.Range((int)Math.Ceiling(low), Math.Max(0, (int)Math.Floor(high) - (int)Math.Ceiling(low) + 1)))
        {
            var value = Math.Pow(10, exponent);
            var y = Y(value);
            _canvas.Children.Add(new Line { X1 = 0, X2 = width, Y1 = y, Y2 = y, Stroke = Palette.Hairline, StrokeThickness = 1 });
            var label = Palette.MonoText(MinerFormatting.FormatDifficulty(value), 11, Palette.TextTertiary);
            Canvas.SetLeft(label, width + 8);
            Canvas.SetTop(label, y - 7);
            _canvas.Children.Add(label);
        }
        foreach (var pool in _samples.Select(s => s.PoolDifficulty).OfType<double>().Distinct().Take(4))
        {
            var y = Y(pool);
            _canvas.Children.Add(new Line { X1 = 0, X2 = width, Y1 = y, Y2 = y, Stroke = Palette.Warning, StrokeThickness = 1, StrokeDashArray = [4, 3] });
        }
        foreach (var sample in _samples)
        {
            var brush = sample.IsSubmitted ? Palette.Accent : Palette.TextTertiary;
            var dot = new Ellipse { Width = 5, Height = 5, Fill = brush };
            Canvas.SetLeft(dot, X(sample.Date) - 2.5);
            Canvas.SetTop(dot, Y(sample.Difficulty) - 2.5);
            _canvas.Children.Add(dot);
        }
    }
}
