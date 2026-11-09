using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using MinerGuru.Core.Models;
using static MinerGuru.Core.Lang;

namespace MinerGuru.Controls;

/// <summary>A miner's stored telemetry over weeks and months: range, metric, chart, key figures and CSV export.</summary>
public sealed partial class LongTermHistoryCard : UserControl
{
    private static readonly HistoryRange[] Ranges = Enum.GetValues<HistoryRange>();
    private static readonly HistoryMetric[] Metrics = Enum.GetValues<HistoryMetric>();

    private readonly ComboBox _rangeBox = new() { MinWidth = 110 };
    private readonly ComboBox _metricBox = new() { MinWidth = 140 };
    private readonly HashrateChart _chart = new() { Height = 200 };
    private readonly TextBlock _figures = Palette.MonoText("", 11, Palette.TextSecondary);
    private readonly TextBlock _status = Palette.UiText("", 12, Palette.TextSecondary);
    private Guid? _minerId;
    private List<HistoryPoint> _points = [];
    private int _load;

    public LongTermHistoryCard()
    {
        foreach (var range in Ranges) _rangeBox.Items.Add(range.Title());
        foreach (var metric in Metrics) _metricBox.Items.Add(metric.Title());
        _rangeBox.SelectedIndex = 1;
        _metricBox.SelectedIndex = 0;
        _rangeBox.SelectionChanged += (_, _) => _ = LoadAsync();
        _metricBox.SelectionChanged += (_, _) => Render();

        var title = PageParts.Title(L("Langzeit-Verlauf", "Long-term history"));
        title.VerticalAlignment = VerticalAlignment.Center;
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
        actions.Children.Add(_metricBox);
        actions.Children.Add(_rangeBox);
        actions.Children.Add(PageParts.TextButton("", L("Als CSV sichern", "Save as CSV"), () => _ = ExportAsync()));
        var header = new Grid();
        header.Children.Add(title);
        header.Children.Add(actions);

        _figures.TextWrapping = TextWrapping.Wrap;
        var body = new StackPanel { Spacing = 12 };
        body.Children.Add(header);
        body.Children.Add(_chart);
        body.Children.Add(_figures);
        body.Children.Add(_status);
        Content = new Border { Child = body, Padding = new Thickness(24), CornerRadius = new CornerRadius(16), Style = PageParts.Resource("CardBorderStyle") };
        ActualThemeChanged += (_, _) =>
        {
            _figures.Foreground = _status.Foreground = Palette.TextSecondary;
            Render();
        };
    }

    private HistoryRange Range => Ranges[Math.Max(0, _rangeBox.SelectedIndex)];
    private HistoryMetric Metric => Metrics[Math.Max(0, _metricBox.SelectedIndex)];

    public void Show(Guid minerId)
    {
        if (_minerId == minerId) return;
        _minerId = minerId;
        _ = LoadAsync();
    }

    private async Task LoadAsync()
    {
        if (_minerId is not { } id) return;
        var load = ++_load;
        var end = DateTime.Now;
        var start = end - Range.Span();
        var points = await Task.Run(() => App.Store.History.Points(id, start, end));
        if (load != _load) return;
        _points = points;
        Render();
    }

    private void Render()
    {
        var metric = Metric;
        var values = _points.Select(p => (p.Time, Value: metric.Value(p))).Where(v => v.Value != null).Select(v => (v.Time, v.Value!.Value)).ToList();
        _chart.ValueLabel = value => metric.Format(value);
        _chart.TimeLabel = Range == HistoryRange.Day ? null : time => time.ToString("d. MMM", Culture);
        AutomationProperties.SetName(_rangeBox, L("Zeitraum", "Period"));
        AutomationProperties.SetName(_metricBox, L("Messwert", "Metric"));
        _chart.GapStepSeconds = HistoryQuery.Step(Range.Span());
        _chart.SetPoints(values);
        _status.Text = "";
        if (values.Count == 0)
        {
            _figures.Text = "";
            _status.Text = L("Noch keine Daten. MinerGuru zeichnet auf, solange es läuft.", "No data yet. MinerGuru records while it is running.");
            return;
        }
        var online = _points.Sum(p => p.OnlineMinutes);
        var recorded = _points.Sum(p => p.RecordedMinutes);
        var uptime = recorded > 0 ? (double)online / recorded * 100 : 0;
        _figures.Text = $"Ø {metric.Format(values.Average(v => v.Value))}   Min {metric.Format(values.Min(v => v.Value))}   Max {metric.Format(values.Max(v => v.Value))}   "
            + L("Betriebszeit ", "Uptime ") + uptime.ToString("0.0", Culture) + " %";
    }

    private async Task ExportAsync()
    {
        if (_minerId is not { } id) return;
        var name = App.Store.Find(id)?.DisplayName ?? "miner";
        var end = DateTime.Now;
        var start = end - Range.Span();
        try
        {
            var points = await Task.Run(() => App.Store.History.Points(id, start, end));
            var text = HistoryCsv.Text(points, _ => name);
            await PageParts.SaveFileAsync($"MinerGuru-{Clean(name)}-{Range}", "CSV", ".csv", System.Text.Encoding.UTF8.GetBytes(text));
        }
        catch (IOException e)
        {
            _status.Text = e.Message;
        }
    }

    private static string Clean(string name) => string.Concat(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '-' : c));
}
