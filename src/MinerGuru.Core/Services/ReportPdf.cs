using System.Globalization;
using System.Text;
using MinerGuru.Core.Models;
using static MinerGuru.Core.Lang;

namespace MinerGuru.Core.Services;

/// <summary>Writes a report as an A4 PDF with the built-in Helvetica, black on white, without any library.</summary>
public static class ReportPdf
{
    private const double PageWidth = 595, PageHeight = 842, Margin = 42;
    private static readonly (string Title, double Width)[] Columns =
    [
        (Strings.Miner, 118), (Strings.Uptime, 50), ("Hashrate", 74), (Strings.Energy, 62),
        (Strings.Cost, 58), ("J/TH", 42), (Strings.AvgC, 42), (Strings.MaxC, 42),
    ];

    private sealed class Canvas
    {
        public readonly List<StringBuilder> Pages = [new()];
        public double Y = PageHeight - Margin;

        private StringBuilder Page => Pages[^1];

        public void Need(double height)
        {
            if (Y - height >= Margin) return;
            Pages.Add(new StringBuilder());
            Y = PageHeight - Margin;
        }

        public void Text(double x, double y, string text, double size = 9, bool bold = false, double gray = 0) =>
            Page.Append(string.Create(CultureInfo.InvariantCulture, $"{gray:0.##} g BT /{(bold ? "F2" : "F1")} {size:0.##} Tf {x:0.##} {y:0.##} Td ({Escape(text)}) Tj ET\n"));

        public void Line(double x1, double y1, double x2, double y2) =>
            Page.Append(string.Create(CultureInfo.InvariantCulture, $"0.8 G 0.5 w {x1:0.##} {y1:0.##} m {x2:0.##} {y2:0.##} l S\n"));

        public void Box(double x, double y, double width, double height) =>
            Page.Append(string.Create(CultureInfo.InvariantCulture, $"0.25 g {x:0.##} {y:0.##} {width:0.##} {height:0.##} re f\n"));
    }

    public static byte[] Render(MinerReport report, PowerTariff tariff)
    {
        var page = new Canvas();
        var dates = string.Create(Culture, $"{report.Start:d} – {report.End.AddSeconds(-1):d}");
        page.Text(Margin, page.Y - 18, Strings.MinerGuruReport, 18, bold: true);
        page.Y -= 36;
        page.Text(Margin, page.Y, dates, 10, gray: 0.35);
        page.Y -= 26;

        var figures = Figures(report, tariff);
        const double FigureWidth = (PageWidth - 2 * Margin) / 4;
        for (var i = 0; i < figures.Count; i++)
        {
            if (i > 0 && i % 4 == 0) page.Y -= 38;
            var x = Margin + i % 4 * FigureWidth;
            page.Text(x, page.Y, figures[i].Label, 8, gray: 0.35);
            page.Text(x, page.Y - 15, figures[i].Value, 12, bold: true);
        }
        page.Y -= 44;

        if (report.Daily.Count > 1) DailyChart(page, report);

        Heading(page, Strings.Miners);
        TableHeader(page);
        foreach (var row in report.Rows)
        {
            if (page.Y - 16 < Margin)
            {
                page.Need(1000);
                TableHeader(page);
            }
            string[] cells =
            [
                Clip(row.Name, 22), Number(row.UptimePercent, "0.0", " %"), row.AverageHashRateGhs is { } hashRate ? HistoryMetric.Hashrate.Format(hashRate) : "—",
                Number(row.EnergyKwh, "0.00", " kWh"), tariff.FormatPrice(row.Cost), Number(row.EfficiencyJPerTh, "0.0"), Number(row.AverageTempC, "0.0"), Number(row.MaxTempC, "0.0"),
            ];
            var x = Margin;
            for (var i = 0; i < cells.Length; i++)
            {
                page.Text(x, page.Y, cells[i], 8.5);
                x += Columns[i].Width;
            }
            page.Y -= 15;
        }

        if (report.Maintenance.Count > 0)
        {
            page.Y -= 10;
            page.Need(50);
            Heading(page, Strings.MaintenanceDone);
            foreach (var record in report.Maintenance)
            {
                page.Need(15);
                page.Text(Margin, page.Y, record.DoneAt.ToString("d", Culture), 8.5, gray: 0.35);
                page.Text(Margin + 70, page.Y, Clip(record.Title, 80), 8.5);
                page.Y -= 15;
            }
        }
        return Assemble(page.Pages);
    }

    /// <summary>The figures at the top of the report, also shown on the reports page.</summary>
    public static List<(string Label, string Value)> Figures(MinerReport report, PowerTariff tariff) =>
    [
        (Strings.AvgHashrate, report.AverageHashRateGhs is { } hashRate ? HistoryMetric.Hashrate.Format(hashRate) : "—"),
        (Strings.OperatingTime, Number(report.UptimePercent, "0.0", " %")),
        (Strings.Energy, Number(report.TotalEnergyKwh, "0.00", " kWh")),
        (Strings.EnergyCost, tariff.FormatPrice(report.TotalCost)),
        (Strings.AvgEfficiency, Number(report.EfficiencyJPerTh, "0.0", " J/TH")),
        (Strings.HighestTemperature, Number(report.MaxTempC, "0.0", " °C")),
        (Strings.AcceptedShares, report.SharesAccepted.ToString("N0", Culture)),
        (Strings.RejectedShares, report.SharesRejected.ToString("N0", Culture)),
    ];

    private static void DailyChart(Canvas page, MinerReport report)
    {
        Heading(page, Strings.FleetHashratePerDay);
        const double Height = 70, Width = PageWidth - 2 * Margin;
        page.Need(Height + 26);
        var top = report.Daily.Max(d => d.HashRateGhs);
        var bar = Width / report.Daily.Count;
        var bottom = page.Y - Height;
        if (top > 0)
            for (var i = 0; i < report.Daily.Count; i++)
                page.Box(Margin + i * bar, bottom, Math.Max(bar - 1.5, 0.8), report.Daily[i].HashRateGhs / top * Height);
        page.Line(Margin, bottom, PageWidth - Margin, bottom);
        page.Text(Margin, bottom - 11, report.Daily[0].Day.ToString("d", Culture), 7.5, gray: 0.35);
        page.Text(PageWidth - Margin - 48, bottom - 11, report.Daily[^1].Day.ToString("d", Culture), 7.5, gray: 0.35);
        page.Text(Margin, page.Y + 1, HistoryMetric.Hashrate.Format(top), 7.5, gray: 0.35);
        page.Y = bottom - 30;
    }

    private static void Heading(Canvas page, string text)
    {
        page.Need(40);
        page.Text(Margin, page.Y, text, 11, bold: true);
        page.Y -= 18;
    }

    private static void TableHeader(Canvas page)
    {
        var x = Margin;
        foreach (var (title, width) in Columns)
        {
            page.Text(x, page.Y, title, 8, bold: true, gray: 0.35);
            x += width;
        }
        page.Line(Margin, page.Y - 4, PageWidth - Margin, page.Y - 4);
        page.Y -= 17;
    }

    private static string Number(double? value, string format, string unit = "") =>
        value is { } number ? number.ToString(format, Culture) + unit : "—";

    private static string Clip(string text, int length) => text.Length <= length ? text : text[..(length - 1)] + "…";

    /// <summary>PDF string escapes; characters outside WinAnsi become question marks.</summary>
    private static string Escape(string text)
    {
        var result = new StringBuilder();
        foreach (var c in text)
        {
            var code = c switch { '€' => 0x80, '…' => 0x85, '–' => 0x96, '—' => 0x97, _ => c <= 0xFF ? c : '?' };
            if (code is '(' or ')' or '\\') result.Append('\\').Append((char)code);
            else if (code is < 32 or > 126) result.Append('\\').Append(Convert.ToString(code, 8).PadLeft(3, '0'));
            else result.Append((char)code);
        }
        return result.ToString();
    }

    private static byte[] Assemble(List<StringBuilder> pages)
    {
        var bytes = new MemoryStream();
        var offsets = new List<long>();
        void Write(string text)
        {
            var data = Encoding.Latin1.GetBytes(text);
            bytes.Write(data);
        }
        void Object(string body)
        {
            offsets.Add(bytes.Position);
            Write($"{offsets.Count} 0 obj\n{body}\nendobj\n");
        }

        Write("%PDF-1.4\n");
        // 1 catalog, 2 page tree, 3 and 4 fonts, then a page and its content per page.
        var kids = string.Join(" ", pages.Select((_, i) => $"{5 + 2 * i} 0 R"));
        Object("<< /Type /Catalog /Pages 2 0 R >>");
        Object($"<< /Type /Pages /Count {pages.Count} /Kids [{kids}] >>");
        Object("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>");
        Object("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica-Bold /Encoding /WinAnsiEncoding >>");
        for (var i = 0; i < pages.Count; i++)
        {
            Object($"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 {PageWidth} {PageHeight}] /Contents {6 + 2 * i} 0 R /Resources << /Font << /F1 3 0 R /F2 4 0 R >> >> >>");
            var content = pages[i].ToString();
            Object($"<< /Length {content.Length} >>\nstream\n{content}endstream");
        }
        var table = bytes.Position;
        Write($"xref\n0 {offsets.Count + 1}\n0000000000 65535 f \n");
        foreach (var offset in offsets) Write($"{offset:0000000000} 00000 n \n");
        Write($"trailer\n<< /Size {offsets.Count + 1} /Root 1 0 R >>\nstartxref\n{table}\n%%EOF\n");
        return bytes.ToArray();
    }
}
