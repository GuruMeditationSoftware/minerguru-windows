using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using MinerGuru.Core.Models;

namespace MinerGuru.Controls;

/// <summary>The color of a tariff zone, by its place in the list.</summary>
public static class ZonePalette
{
    public static Brush Color(int index) => (index % 6) switch
    {
        0 => Palette.Accent,
        1 => Palette.IsLight ? Palette.Rgb(0x3b, 0x6f, 0xd4) : Palette.Rgb(0x6f, 0x9b, 0xf0),
        2 => Palette.Good,
        3 => Palette.IsLight ? Palette.Rgb(0x8a, 0x56, 0xc9) : Palette.Rgb(0xb1, 0x8a, 0xe8),
        4 => Palette.IsLight ? Palette.Rgb(0x1f, 0x9a, 0xa8) : Palette.Rgb(0x4c, 0xc6, 0xd3),
        _ => Palette.Bad,
    };
}

/// <summary>
/// One row per weekday, colored by the zone that applies, as the Mac's WeekTariffBar: base price in grey,
/// the current moment marked, hours below.
/// </summary>
public sealed partial class WeekTariffBar : Grid
{
    private const double DayColumnWidth = 28;
    private const double HourLabelWidth = 24;

    private readonly StackPanel _days = new() { Spacing = 4 };
    private readonly Canvas _hours = new() { Height = 12, Margin = new Thickness(DayColumnWidth + 8, 0, 0, 0) };

    public WeekTariffBar()
    {
        var stack = new StackPanel { Spacing = 4 };
        stack.Children.Add(_days);
        stack.Children.Add(_hours);
        Children.Add(stack);
        _hours.SizeChanged += (_, _) => PlaceHours();
    }

    public void Update(PowerTariff tariff, DateTime now)
    {
        _days.Children.Clear();
        foreach (var day in WeekdayOrder.MondayFirst)
        {
            var today = day == now.DayOfWeek;
            var row = new Grid { ColumnSpacing = 8 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(DayColumnWidth) });
            row.ColumnDefinitions.Add(new ColumnDefinition());
            var name = Palette.MonoText(WeekdayOrder.ShortName(day), 11, today ? Palette.TextPrimary : Palette.TextTertiary);
            name.VerticalAlignment = VerticalAlignment.Center;
            row.Children.Add(name);

            var strip = Strip(tariff, day);
            if (today) strip.Children.Add(NowMarker(now, strip.ColumnDefinitions.Count));
            SetColumn(strip, 1);
            row.Children.Add(strip);
            _days.Children.Add(row);
        }
        PlaceHours();
    }

    private static Grid Strip(PowerTariff tariff, DayOfWeek day)
    {
        var segments = tariff.Segments(day);
        var strip = new Grid { Height = 14 };
        for (var i = 0; i < segments.Count; i++)
        {
            var segment = segments[i];
            strip.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(segment.EndMinute - segment.StartMinute, GridUnitType.Star) });
            // A Border does not clip its content, so the ends get their own radius.
            var block = new Border
            {
                Background = Fill(tariff, segment.ZoneId),
                CornerRadius = new CornerRadius(i == 0 ? 3 : 0, i == segments.Count - 1 ? 3 : 0, i == segments.Count - 1 ? 3 : 0, i == 0 ? 3 : 0),
            };
            ToolTipService.SetToolTip(block, Label(tariff, segment));
            SetColumn(block, i);
            strip.Children.Add(block);
        }
        return strip;
    }

    /// <summary>Spans the strip: its own two columns would mix with the segments, so it uses a grid of its own.</summary>
    private static Grid NowMarker(DateTime now, int columns)
    {
        var minute = now.Hour * 60 + now.Minute;
        var marker = new Grid { IsHitTestVisible = false };
        marker.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(minute, GridUnitType.Star) });
        marker.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(WeeklyWindow.MinutesPerDay - minute, GridUnitType.Star) });
        var line = new Rectangle { Width = 2, Fill = Palette.TextPrimary, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(-1, 0, 0, 0) };
        SetColumn(line, 1);
        marker.Children.Add(line);
        SetColumnSpan(marker, columns);
        return marker;
    }

    private static Brush Fill(PowerTariff tariff, Guid? zoneId)
    {
        var index = tariff.Zones.FindIndex(z => z.Id == zoneId);
        if (index < 0) return Palette.IsLight ? Palette.Black(0.12) : Palette.White(0.12);
        return new SolidColorBrush(((SolidColorBrush)ZonePalette.Color(index)).Color) { Opacity = 0.85 };
    }

    private static string Label(PowerTariff tariff, TariffSegment segment)
    {
        var name = tariff.Zones.FirstOrDefault(z => z.Id == segment.ZoneId)?.Name ?? Strings.BasePrice;
        return $"{name} {WeeklyWindow.Clock(segment.StartMinute)}–{WeeklyWindow.Clock(segment.EndMinute)}";
    }

    private void PlaceHours()
    {
        _hours.Children.Clear();
        foreach (var hour in new[] { 0, 6, 12, 18, 24 })
        {
            var label = Palette.MonoText(hour.ToString(), 10, Palette.TextTertiary);
            label.Width = HourLabelWidth;
            label.TextAlignment = TextAlignment.Center;
            Canvas.SetLeft(label, _hours.ActualWidth * hour / 24 - HourLabelWidth / 2);
            _hours.Children.Add(label);
        }
    }
}
