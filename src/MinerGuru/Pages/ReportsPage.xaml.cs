using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Shapes;
using MinerGuru.Controls;
using MinerGuru.Core.Models;
using MinerGuru.Core.Services;
using static MinerGuru.Core.Lang;

namespace MinerGuru.Pages;

/// <summary>Fleet reports from the long-term history, for the screen, as CSV and as PDF.</summary>
public sealed partial class ReportsPage : Page, IHeaderPage
{
    private static readonly ReportPeriod[] Periods = Enum.GetValues<ReportPeriod>();
    private static readonly double[] ColumnWidths = [3, 1.1, 1.5, 1.4, 1.3, 1, 1, 1];

    private readonly ComboBox _periodBox = new() { MinWidth = 180 };
    private readonly Button _csv;
    private readonly Button _pdf;
    private readonly TextBlock _dates = Palette.MonoText("", 11, Palette.TextSecondary);
    private readonly TextBlock _status = Palette.UiText("", 13, Palette.TextSecondary);
    private readonly AdaptiveGrid _figures = new() { MinColumnWidth = 180, Spacing = 12 };
    private readonly HashrateChart _chart = new() { Height = 180, TimeLabel = time => time.ToString("d. MMM", Culture) };
    private readonly StackPanel _rows = new() { Spacing = 0 };
    private readonly StackPanel _maintenance = new();
    private readonly StackPanel _content = new() { Spacing = 16 };
    private readonly Border _maintenanceCard;
    private MinerReport? _report;
    private int _load;

    public ReportsPage()
    {
        InitializeComponent();
        AutomationProperties.SetName(_periodBox, L("Zeitraum", "Period"));
        foreach (var period in Periods) _periodBox.Items.Add(period.Title());
        _periodBox.SelectedIndex = 1;
        _periodBox.SelectionChanged += (_, _) => _ = LoadAsync();
        _csv = PageParts.TextButton("", "CSV", () => _ = ExportCsvAsync());
        _pdf = PageParts.TextButton("", "PDF", () => _ = ExportPdfAsync());

        _dates.VerticalAlignment = VerticalAlignment.Center;
        var toolbar = PageParts.Row(1, 4);
        toolbar.Children.Add(_periodBox);
        toolbar.Children.Add(_dates.At(1));
        toolbar.Children.Add(_csv.At(2));
        toolbar.Children.Add(_pdf.At(3));

        var chart = new StackPanel { Spacing = 10 };
        chart.Children.Add(PageParts.Title(L("Hashrate der Flotte pro Tag", "Fleet hashrate per day")));
        chart.Children.Add(_chart);
        var table = new StackPanel { Spacing = 8 };
        table.Children.Add(PageParts.Title("Miner"));
        table.Children.Add(_rows);
        var maintenance = new StackPanel { Spacing = 8 };
        maintenance.Children.Add(PageParts.Title(L("Erledigte Wartung", "Maintenance done")));
        maintenance.Children.Add(_maintenance);
        _maintenanceCard = PageParts.Card(maintenance);

        _content.Children.Add(_figures);
        _content.Children.Add(PageParts.Card(chart));
        _content.Children.Add(PageParts.Card(table));
        _content.Children.Add(_maintenanceCard);

        _status.TextWrapping = TextWrapping.Wrap;
        var page = new StackPanel { Spacing = 16, MaxWidth = (double)Application.Current.Resources["PageMaxWidth"], Padding = (Thickness)Application.Current.Resources["PagePadding"] };
        page.Children.Add(toolbar);
        page.Children.Add(_status);
        page.Children.Add(_content);
        Content = new ScrollViewer { Content = page };

        Loaded += (_, _) => _ = LoadAsync();
        ActualThemeChanged += (_, _) => Render();
    }

    public (string Title, string Subtitle) Header => (L("Berichte", "Reports"), L("Betrieb, Energie und Kosten der Flotte", "Uptime, energy and cost of the fleet"));

    private static PowerTariff Tariff => App.Store.Power.Tariff;
    private ReportPeriod Period => Periods[Math.Max(0, _periodBox.SelectedIndex)];

    private async Task LoadAsync()
    {
        var load = ++_load;
        var (start, end) = Period.Interval(DateTime.Now);
        var points = await Task.Run(() => App.Store.History.HourlyPoints(start, end));
        if (load != _load) return;
        var names = App.Store.Miners.ToDictionary(m => m.Id, m => m.DisplayName);
        _report = MinerReport.Build(start, end, points, id => names.GetValueOrDefault(id), Tariff.Price, App.Store.Maintenance.Records);
        Render();
    }

    private void Render()
    {
        var report = _report;
        var hasData = report is { Rows.Count: > 0 };
        _csv.IsEnabled = _pdf.IsEnabled = hasData;
        _content.Visibility = hasData ? Visibility.Visible : Visibility.Collapsed;
        _status.Foreground = _dates.Foreground = Palette.TextSecondary;
        _status.Text = hasData || report == null ? "" : L("Keine Daten in diesem Zeitraum. Berichte entstehen aus dem Langzeit-Verlauf.", "No data for this period. Reports are built from the long-term history.");
        _status.Visibility = _status.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (report == null) return;
        _dates.Text = string.Create(Culture, $"{report.Start:d} – {report.End.AddSeconds(-1):d}");
        if (!hasData) return;

        _figures.Children.Clear();
        foreach (var (label, value) in ReportPdf.Figures(report, Tariff))
        {
            var figure = new PowerFigure(label);
            figure.Update(value, "");
            _figures.Children.Add(figure);
        }

        _chart.SetPoints(report.Daily.Select(d => (d.Day, d.HashRateGhs)));

        _rows.Children.Clear();
        _rows.Children.Add(TableRow([L("Miner", "Miner"), L("Betrieb", "Uptime"), "Hashrate", L("Energie", "Energy"), L("Kosten", "Cost"), "J/TH", "Ø °C", "Max °C"], header: true));
        foreach (var r in report.Rows)
        {
            _rows.Children.Add(new Rectangle { Height = 1, Fill = Palette.Hairline });
            _rows.Children.Add(TableRow(
            [
                r.Name, Number(r.UptimePercent, "0.0", " %"), r.AverageHashRateGhs is { } hashRate ? HistoryMetric.Hashrate.Format(hashRate) : "—",
                Number(r.EnergyKwh, "0.00", " kWh"), Tariff.FormatPrice(r.Cost), Number(r.EfficiencyJPerTh, "0.0"), Number(r.AverageTempC, "0.0"), Number(r.MaxTempC, "0.0"),
            ], header: false));
        }

        _maintenanceCard.Visibility = report.Maintenance.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        _maintenance.Children.Clear();
        foreach (var record in report.Maintenance)
        {
            var row = PageParts.Row(1, 2);
            var date = Palette.MonoText(record.DoneAt.ToString("d", Culture), 11, Palette.TextSecondary);
            date.Width = 80;
            date.VerticalAlignment = VerticalAlignment.Center;
            row.Children.Add(date);
            row.Children.Add(Palette.UiText(record.Title, 13, Palette.TextPrimary).At(1));
            _maintenance.Children.Add(row);
        }
    }

    private static Grid TableRow(string[] cells, bool header)
    {
        var row = new Grid { Padding = new Thickness(0, 6, 0, 6), ColumnSpacing = 8 };
        for (var i = 0; i < cells.Length; i++)
        {
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(ColumnWidths[i], GridUnitType.Star) });
            var cell = i == 0 && !header ? Palette.UiText(cells[i], 13, Palette.TextPrimary) : Palette.MonoText(cells[i], 11, header ? Palette.TextSecondary : Palette.TextPrimary);
            cell.TextTrimming = TextTrimming.CharacterEllipsis;
            cell.VerticalAlignment = VerticalAlignment.Center;
            row.Children.Add(cell.At(i));
        }
        return row;
    }

    private static string Number(double? value, string format, string unit = "") => value is { } number ? number.ToString(format, Culture) + unit : "—";

    private string FileName => $"MinerGuru-{L("Bericht", "Report")}-{_report!.Start:yyyy-MM-dd}";

    private async Task ExportCsvAsync()
    {
        if (_report is { } report) await SaveAsync("CSV", ".csv", System.Text.Encoding.UTF8.GetBytes(report.CsvText()));
    }

    private async Task ExportPdfAsync()
    {
        if (_report is { } report) await SaveAsync("PDF", ".pdf", ReportPdf.Render(report, Tariff));
    }

    private async Task SaveAsync(string type, string extension, byte[] content)
    {
        try
        {
            await PageParts.SaveFileAsync(FileName, type, extension, content);
        }
        catch (IOException e)
        {
            _status.Text = e.Message;
            _status.Visibility = Visibility.Visible;
        }
    }
}
