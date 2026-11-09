using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Path = Microsoft.UI.Xaml.Shapes.Path;
using MinerGuru.Core;
using MinerGuru.Core.Models;
using Windows.Foundation;

namespace MinerGuru.Controls;

/// <summary>
/// Hashrate over the last refresh rounds: one accent line (monotone curve, like Swift Charts' <c>.monotone</c>)
/// with a faint area on a muted grid. With <see cref="ShowAxis"/> it carries time labels below and three hashrate
/// labels on the right, as the Mac fleet chart; without, it is the sparkline of the miner cards.
/// </summary>
public sealed partial class HashrateChart : Grid
{
    private readonly Canvas _canvas = new();
    private readonly Border _placeholder;
    private IReadOnlyList<(DateTime Time, double Value)> _points = [];

    public HashrateChart()
    {
        _placeholder = new Border
        {
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Child = new TextBlock
            {
                Text = Strings.TheHistoryFillsUpWith,
                FontSize = 12,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                TextWrapping = TextWrapping.Wrap,
                TextAlignment = TextAlignment.Center,
                Margin = new Thickness(8),
            },
        };
        Children.Add(_canvas);
        Children.Add(_placeholder);
        SizeChanged += (_, _) => Redraw();
        ActualThemeChanged += (_, _) => Redraw();
    }

    public bool ShowAxis { get; set; } = true;

    /// <summary>Labels of the value axis; hashrate by default.</summary>
    public Func<double, string> ValueLabel { get; set; } = MinerFormatting.FormatHashrate;

    /// <summary>Line colour; the accent by default.</summary>
    public Func<Brush>? LineBrush { get; set; }

    /// <summary>Second, dashed line in the same scale (VR temperature).</summary>
    public IReadOnlyList<(DateTime Time, double Value)> SecondLine { get; set; } = [];

    /// <summary>Dashed reference line (temperature warning).</summary>
    public double? Reference { get; set; }

    /// <summary>Faint area under the line; the sparklines draw the line only.</summary>
    public bool ShowArea { get; set; } = true;

    /// <summary>Hides the "fills up" hint, for the small charts in the cards.</summary>
    public bool ShowPlaceholder { get; set; } = true;

    /// <summary>Row distance of the data; set, a stretch without rows breaks the line instead of bridging it.</summary>
    public int? GapStepSeconds { get; set; }

    /// <summary>Text of a time label; the time of day unless a chart spans days.</summary>
    public Func<DateTime, string>? TimeLabel { get; set; }

    public void SetPoints(IEnumerable<(DateTime Time, double Value)> points)
    {
        _points = points.ToList();
        Redraw();
    }

    private void Redraw()
    {
        _canvas.Children.Clear();
        var enough = _points.Count >= 2;
        _placeholder.Visibility = !enough && ShowPlaceholder ? Visibility.Visible : Visibility.Collapsed;
        _placeholder.BorderBrush = Palette.Hairline;
        ((TextBlock)_placeholder.Child).Foreground = Palette.TextTertiary;
        if (!enough || ActualWidth < 20 || ActualHeight < 10) return;

        var axisWidth = ShowAxis ? 104.0 : 0;
        var axisHeight = ShowAxis ? 22.0 : 0;
        var width = ActualWidth - axisWidth;
        var height = ActualHeight - axisHeight;

        // Room above and below, so a flat line sits in the middle and small changes still show.
        var all = _points.Concat(SecondLine).Select(p => p.Value).Concat(Reference is { } reference ? [reference] : Array.Empty<double>()).ToList();
        var low = all.Min();
        var high = all.Max();
        var pad = Math.Max(Math.Max((high - low) * 0.6, high * 0.02), 1);
        var min = Math.Max(0, low - pad);
        var max = high + pad;
        var start = _points[0].Time;
        var end = _points[^1].Time;
        var span = Math.Max(1, (end - start).TotalSeconds);

        double X(DateTime t) => (t - start).TotalSeconds / span * width;
        double Y(double v) => height - (v - min) / (max - min) * height;
        var runs = (GapStepSeconds is { } gapStep ? HistoryQuery.SplitAtGaps(_points, gapStep) : [_points.ToList()])
            .Select(run => run.Select(p => new Point(X(p.Time), Y(p.Value))).ToList()).ToList();

        if (ShowAxis)
        {
            var hairline = Palette.Hairline;
            var muted = Palette.TextTertiary;
            foreach (var value in NiceTicks(min, max, 3))
            {
                var y = Y(value);
                _canvas.Children.Add(new Line { X1 = 0, X2 = width, Y1 = y, Y2 = y, Stroke = hairline, StrokeThickness = 1 });
                AddLabel(ValueLabel(value), width + 8, y - 7, muted);
            }
            var withSeconds = span < 600;
            var step = span / 5;
            for (var i = 1; i < 5; i++)
            {
                var time = start.AddSeconds(step * i);
                var x = X(time);
                _canvas.Children.Add(new Line { X1 = x, X2 = x, Y1 = 0, Y2 = height, Stroke = hairline, StrokeThickness = 1, StrokeDashArray = [2, 3] });
                var label = TimeLabel?.Invoke(time) ?? time.ToString(withSeconds ? "HH:mm:ss" : "HH:mm", Lang.Culture);
                AddLabel(label, x - label.Length * 3.1, height + 6, muted);
            }
        }

        if (ShowArea)
        {
            foreach (var run in runs.Where(r => r.Count >= 2))
            {
                var area = MonotoneFigure(run);
                area.Segments.Add(new LineSegment { Point = new Point(run[^1].X, height) });
                area.Segments.Add(new LineSegment { Point = new Point(run[0].X, height) });
                area.IsClosed = true;
                var areaGeometry = new PathGeometry();
                areaGeometry.Figures.Add(area);
                _canvas.Children.Add(new Path { Data = areaGeometry, Fill = Palette.Get("ChartAreaBrush") });
            }
        }
        if (Reference is { } limit)
            _canvas.Children.Add(new Line { X1 = 0, X2 = width, Y1 = Y(limit), Y2 = Y(limit), Stroke = Palette.Warning, StrokeThickness = 1, StrokeDashArray = [3, 3] });
        var second = SecondLine.Where(p => p.Time >= start && p.Time <= end).Select(p => new Point(X(p.Time), Y(p.Value))).ToList();
        if (second.Count >= 2)
        {
            var secondGeometry = new PathGeometry();
            secondGeometry.Figures.Add(MonotoneFigure(second));
            _canvas.Children.Add(new Path { Data = secondGeometry, Stroke = Palette.TextTertiary, StrokeThickness = 1.5, StrokeDashArray = [4, 3] });
        }
        var lineGeometry = new PathGeometry();
        foreach (var run in runs.Where(r => r.Count >= 2)) lineGeometry.Figures.Add(MonotoneFigure(run));
        var lineBrush = LineBrush?.Invoke() ?? Palette.Accent;
        _canvas.Children.Add(new Path
        {
            Data = lineGeometry,
            Stroke = lineBrush,
            StrokeThickness = 1.5,
            StrokeLineJoin = PenLineJoin.Round,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
        });
        // A lone value between two gaps has no line to sit on.
        foreach (var run in runs.Where(r => r.Count == 1))
        {
            var dot = new Ellipse { Width = 3, Height = 3, Fill = lineBrush };
            Canvas.SetLeft(dot, run[0].X - 1.5f);
            Canvas.SetTop(dot, run[0].Y - 1.5f);
            _canvas.Children.Add(dot);
        }
    }

    /// <summary>Monotone cubic curve (Fritsch–Carlson): smooth, but never overshoots between two samples.</summary>
    private static PathFigure MonotoneFigure(IReadOnlyList<Point> p)
    {
        var figure = new PathFigure { StartPoint = p[0], IsClosed = false };
        var n = p.Count;
        var slopes = new double[n - 1];
        for (var i = 0; i < n - 1; i++)
        {
            var dx = p[i + 1].X - p[i].X;
            slopes[i] = dx == 0 ? 0 : (p[i + 1].Y - p[i].Y) / dx;
        }
        var tangents = new double[n];
        tangents[0] = slopes[0];
        tangents[n - 1] = slopes[n - 2];
        for (var i = 1; i < n - 1; i++)
            tangents[i] = slopes[i - 1] * slopes[i] <= 0 ? 0 : (slopes[i - 1] + slopes[i]) / 2;
        for (var i = 0; i < n - 1; i++)
        {
            if (slopes[i] == 0) { tangents[i] = tangents[i + 1] = 0; continue; }
            var a = tangents[i] / slopes[i];
            var b = tangents[i + 1] / slopes[i];
            var h = a * a + b * b;
            if (h > 9)
            {
                var t = 3 / Math.Sqrt(h);
                tangents[i] = t * a * slopes[i];
                tangents[i + 1] = t * b * slopes[i];
            }
        }
        for (var i = 0; i < n - 1; i++)
        {
            var dx = (p[i + 1].X - p[i].X) / 3;
            figure.Segments.Add(new BezierSegment
            {
                Point1 = new Point(p[i].X + dx, p[i].Y + tangents[i] * dx),
                Point2 = new Point(p[i + 1].X - dx, p[i + 1].Y - tangents[i + 1] * dx),
                Point3 = p[i + 1],
            });
        }
        return figure;
    }

    /// <summary>About <paramref name="count"/> round values between min and max, e.g. 16.00 / 16.50 / 17.00 TH/s.</summary>
    private static IEnumerable<double> NiceTicks(double min, double max, int count)
    {
        var raw = (max - min) / count;
        var magnitude = Math.Pow(10, Math.Floor(Math.Log10(raw)));
        var step = new[] { 1, 2, 2.5, 5, 10 }.Select(f => f * magnitude).First(s => s >= raw);
        for (var value = Math.Ceiling(min / step) * step; value <= max; value += step)
            yield return value;
    }

    private void AddLabel(string text, double x, double y, Brush brush)
    {
        var label = new TextBlock
        {
            Text = text,
            FontFamily = Palette.Mono,
            FontSize = 10,
            Foreground = brush,
        };
        Canvas.SetLeft(label, x);
        Canvas.SetTop(label, y);
        _canvas.Children.Add(label);
    }
}
