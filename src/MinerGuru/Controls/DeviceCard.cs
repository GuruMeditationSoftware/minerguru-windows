using System.Numerics;
using Microsoft.UI;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Path = Microsoft.UI.Xaml.Shapes.Path;
using MinerGuru.Core.Models;
using Windows.Foundation;
using Windows.UI;

namespace MinerGuru.Controls;

/// <summary>
/// A miner drawn as a card-sized board seen from above: dark solder mask, silver ASIC lids, a small display and
/// the model on the silkscreen. Like a bank card it is an object, so it stays dark in light mode. Details drop away
/// at small sizes. Same geometry as the Mac app's DeviceCardView.
/// </summary>
public sealed partial class DeviceCard : Canvas
{
    public const double Aspect = 1.586;

    private readonly double _w;
    private readonly double _h;
    private readonly bool _detailed;
    private readonly StatusBlock? _led;
    private DeviceCardSpec? _spec;

    public DeviceCard(double width, bool showLed = true)
    {
        _w = width;
        _h = width / Aspect;
        _detailed = width >= 80;
        Width = _w;
        Height = _h;
        if (showLed) _led = new StatusBlock(_detailed ? Math.Max(6, _w * 0.04) : 6);
    }

    /// <summary>Redraws the board when the spec changed and sets the LED.</summary>
    public void Update(DeviceCardSpec spec, ConnectionState state, bool hot, string? offlineReason = null)
    {
        if (spec != _spec)
        {
            _spec = spec;
            Draw(spec);
        }
        _led?.Apply(state, hot, offlineReason);
    }

    public void Update(MinerDevice miner, bool hot) => Update(DeviceCardSpec.For(miner), miner.State, hot, miner.OfflineReason);

    private void Draw(DeviceCardSpec spec)
    {
        Children.Clear();
        var w = _w;
        var h = _h;
        var radius = Math.Max(3, w * 0.07);

        if (_detailed) AddShadow(radius);

        // Solder mask: graphite with a soft light from the top left. NerdOS boards are a touch cooler.
        Children.Add(new Rectangle
        {
            Width = w,
            Height = h,
            RadiusX = radius,
            RadiusY = radius,
            Fill = Gradient(new Point(0, 0), new Point(1, 1), spec.HasTft
                ? [Rgb(0.17, 0.18, 0.21), Rgb(0.09, 0.10, 0.12), Rgb(0.06, 0.06, 0.08)]
                : [Gray(0.21), Gray(0.11), Gray(0.07)]),
        });

        if (_detailed)
        {
            AddTraces();
            AddPort();
            AddDisplay(spec);
        }

        AddChips(spec);

        if (w >= 120)
        {
            Place(new TextBlock
            {
                Text = spec.Family,
                FontSize = w * 0.062,
                FontWeight = Microsoft.UI.Text.FontWeights.Medium,
                Foreground = new SolidColorBrush(WhiteAlpha(0.72)),
            }, w * 0.07, h * 0.06);
            Place(new TextBlock
            {
                Text = spec.ModelLine,
                FontFamily = Palette.Mono,
                FontSize = w * 0.046,
                Foreground = new SolidColorBrush(WhiteAlpha(0.5)),
                Width = w * 0.86,
                TextTrimming = TextTrimming.CharacterEllipsis,
            }, w * 0.07, h * 0.81);
        }

        if (_led != null)
        {
            var size = _led.Width;
            Place(_led, w - size - (_detailed ? w * 0.07 : 3), _detailed ? h * 0.1 : 3);
        }

        // Edge catches the light at the top
        Children.Add(new Rectangle
        {
            Width = w,
            Height = h,
            RadiusX = radius,
            RadiusY = radius,
            StrokeThickness = w >= 60 ? 1 : 0.5,
            Stroke = Gradient(new Point(0.5, 0), new Point(0.5, 1), [WhiteAlpha(0.22), WhiteAlpha(0.04)]),
        });
    }

    // Parts

    /// <summary>Area the ASICs share: right of the display on detailed cards, centered on small ones.</summary>
    private Rect ChipArea => _detailed
        ? new Rect(_w * 0.36, _h * 0.2, _w * 0.56, _h * 0.58)
        : new Rect(_w * 0.12, _h * 0.16, _w * 0.76, _h * 0.68);

    private void AddChips(DeviceCardSpec spec)
    {
        var area = ChipArea;
        var (columns, rows) = spec.ChipGrid;
        var cell = Math.Min(area.Width / columns, area.Height / rows);
        var chip = Math.Min(cell * 0.74, _h * 0.38);
        var originX = area.X + area.Width / 2 - cell * columns / 2;
        var originY = area.Y + area.Height / 2 - cell * rows / 2;
        for (var i = 0; i < Math.Max(1, spec.ChipCount); i++)
        {
            var x = originX + i % columns * cell + (cell - chip) / 2;
            var y = originY + i / columns * cell + (cell - chip) / 2;
            AddAsicLid(x, y, chip, chip >= 30 ? spec.ChipModel : null);
        }
    }

    /// <summary>Nickel-plated lid of one ASIC with its laser-etched model.</summary>
    private void AddAsicLid(double x, double y, double size, string? label)
    {
        var radius = Math.Max(1, size * 0.1);
        // Small contact shadow under the lid
        Place(new Rectangle { Width = size, Height = size, RadiusX = radius, RadiusY = radius, Fill = new SolidColorBrush(BlackAlpha(0.35)) }, x, y + size * 0.04);
        Place(new Rectangle
        {
            Width = size,
            Height = size,
            RadiusX = radius,
            RadiusY = radius,
            Fill = Gradient(new Point(0, 0), new Point(1, 1), [Gray(0.86), Gray(0.6), Gray(0.76)]),
        }, x, y);
        var inset = size * 0.12;
        var innerRadius = Math.Max(1, size * 0.06);
        Place(new Rectangle
        {
            Width = size - 2 * inset,
            Height = size - 2 * inset,
            RadiusX = innerRadius,
            RadiusY = innerRadius,
            Stroke = new SolidColorBrush(BlackAlpha(0.18)),
            StrokeThickness = Math.Max(0.5, size * 0.025),
        }, x + inset, y + inset);
        if (label != null)
        {
            var text = new TextBlock
            {
                Text = label,
                FontFamily = Palette.Mono,
                FontSize = size * 0.15,
                FontWeight = Microsoft.UI.Text.FontWeights.Medium,
                Foreground = new SolidColorBrush(BlackAlpha(0.38)),
                Width = size,
                TextAlignment = TextAlignment.Center,
                TextTrimming = TextTrimming.Clip,
            };
            Place(text, x, y + size / 2 - size * 0.1);
        }
    }

    private void AddTraces()
    {
        var area = ChipArea;
        var w = _w;
        var h = _h;
        var geometry = new PathGeometry();
        for (var step = 0; step < 4; step++)
        {
            var y = area.Y + area.Height * (0.2 + 0.2 * step);
            geometry.Figures.Add(Figure(new(w * 0.04, y), new(w * 0.3, y), new(area.X - w * 0.02, y + h * 0.03)));
        }
        var midX = area.X + area.Width / 2;
        geometry.Figures.Add(Figure(new(midX, area.Bottom + h * 0.02), new(midX + w * 0.06, h * 0.96)));
        geometry.Figures.Add(Figure(new(area.Right - w * 0.04, area.Y - h * 0.02), new(area.Right, h * 0.04)));
        Children.Add(new Path
        {
            Data = geometry,
            Stroke = new SolidColorBrush(Color.FromArgb((byte)(0.09 * 255), (byte)(0.85 * 255), (byte)(0.6 * 255), (byte)(0.4 * 255))),
            StrokeThickness = Math.Max(0.5, w * 0.005),
        });
    }

    /// <summary>USB-C socket at the left edge.</summary>
    private void AddPort()
    {
        var radius = _w * 0.012;
        Place(new Rectangle
        {
            Width = _w * 0.035,
            Height = _h * 0.14,
            RadiusX = radius,
            RadiusY = radius,
            Fill = Gradient(new Point(0.5, 0), new Point(0.5, 1), [Gray(0.7), Gray(0.45)]),
        }, 0, _h * 0.43);
    }

    /// <summary>Bitaxe boards carry a small OLED, NerdOS boards a colour TFT that shows the hashrate.</summary>
    private void AddDisplay(DeviceCardSpec spec)
    {
        var w = _w;
        var h = _h;
        if (!spec.HasTft)
        {
            double x = w * 0.09, y = h * 0.42, dw = w * 0.19, dh = h * 0.13, r = w * 0.008;
            Place(new Rectangle { Width = dw, Height = dh, RadiusX = r, RadiusY = r, Fill = new SolidColorBrush(Colors.Black), Stroke = new SolidColorBrush(WhiteAlpha(0.1)), StrokeThickness = 0.5 }, x, y);
            var bar = Math.Max(0.5, h * 0.018);
            var gap = h * 0.025;
            var top = y + (dh - 2 * bar - gap) / 2;
            Place(new Rectangle { Width = w * 0.09, Height = bar, RadiusX = bar / 2, RadiusY = bar / 2, Fill = new SolidColorBrush(WhiteAlpha(0.55)) }, x + w * 0.018, top);
            Place(new Rectangle { Width = w * 0.13, Height = bar, RadiusX = bar / 2, RadiusY = bar / 2, Fill = new SolidColorBrush(WhiteAlpha(0.3)) }, x + w * 0.018, top + bar + gap);
        }
        else
        {
            double x = w * 0.08, y = h * 0.32, dw = w * 0.21, dh = h * 0.36, r = w * 0.012;
            Place(new Rectangle { Width = dw, Height = dh, RadiusX = r, RadiusY = r, Fill = new SolidColorBrush(Color.FromArgb(255, 8, 10, 15)), Stroke = new SolidColorBrush(WhiteAlpha(0.12)), StrokeThickness = 0.5 }, x, y);
            double lw = w * 0.17, lh = h * 0.26;
            double lx = x + (dw - lw) / 2, ly = y + (dh - lh) / 2;
            var line = new Polyline
            {
                Stroke = Palette.Accent,
                Opacity = 0.9,
                StrokeThickness = Math.Max(0.75, w * 0.007),
                StrokeLineJoin = PenLineJoin.Round,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
            };
            foreach (var (px, py) in new[] { (0, 0.7), (0.2, 0.55), (0.38, 0.62), (0.55, 0.38), (0.74, 0.45), (1, 0.28) })
                line.Points.Add(new Point(lx + px * lw, ly + py * lh));
            Children.Add(line);
        }
    }

    /// <summary>Soft drop shadow under the board, as a composition shadow of a rounded rectangle.</summary>
    private void AddShadow(double radius)
    {
        var host = new Canvas { Width = _w, Height = _h, IsHitTestVisible = false };
        Children.Add(host);
        var compositor = ElementCompositionPreview.GetElementVisual(host).Compositor;
        var geometry = compositor.CreateRoundedRectangleGeometry();
        geometry.Size = new Vector2((float)_w, (float)_h);
        geometry.CornerRadius = new Vector2((float)radius);
        var shape = compositor.CreateSpriteShape(geometry);
        shape.FillBrush = compositor.CreateColorBrush(Colors.Black);
        var shapes = compositor.CreateShapeVisual();
        shapes.Size = geometry.Size;
        shapes.Shapes.Add(shape);
        var shadow = compositor.CreateDropShadow();
        shadow.BlurRadius = (float)(_w * 0.12);
        shadow.Offset = new Vector3(0, (float)(_w * 0.04), 0);
        shadow.Color = BlackAlpha(0.35);
        var layer = compositor.CreateLayerVisual();
        layer.Size = geometry.Size;
        layer.Shadow = shadow;
        layer.Children.InsertAtTop(shapes);
        ElementCompositionPreview.SetElementChildVisual(host, layer);
    }

    // Helpers

    private void Place(UIElement element, double x, double y)
    {
        SetLeft(element, x);
        SetTop(element, y);
        Children.Add(element);
    }

    private static PathFigure Figure(params Point[] points)
    {
        var figure = new PathFigure { StartPoint = points[0], IsClosed = false };
        foreach (var point in points.Skip(1)) figure.Segments.Add(new LineSegment { Point = point });
        return figure;
    }

    private static LinearGradientBrush Gradient(Point start, Point end, Color[] colors)
    {
        var brush = new LinearGradientBrush { StartPoint = start, EndPoint = end };
        for (var i = 0; i < colors.Length; i++)
            brush.GradientStops.Add(new GradientStop { Color = colors[i], Offset = colors.Length == 1 ? 0 : i / (double)(colors.Length - 1) });
        return brush;
    }

    private static Color Gray(double white) => Rgb(white, white, white);
    private static Color Rgb(double r, double g, double b) => Color.FromArgb(255, (byte)(r * 255), (byte)(g * 255), (byte)(b * 255));
    private static Color WhiteAlpha(double a) => Color.FromArgb((byte)(a * 255), 255, 255, 255);
    private static Color BlackAlpha(double a) => Color.FromArgb((byte)(a * 255), 0, 0, 0);
}

/// <summary>Up to three device cards fanned out like cards in a hand, the strongest miner in front.</summary>
public sealed partial class DeviceCardStack : Canvas
{
    private readonly double _cardWidth;
    private string _key = "";

    public DeviceCardStack(double cardWidth = 176)
    {
        _cardWidth = cardWidth;
        Width = cardWidth * 1.4;
        Height = cardWidth / DeviceCard.Aspect * 1.5;
        IsHitTestVisible = false;
    }

    public void Update(IReadOnlyList<(DeviceCardSpec Spec, ConnectionState State, bool Hot)> cards)
    {
        var shown = cards.Take(3).ToList();
        var key = string.Join("|", shown.Select(c => $"{c.Spec}{c.State}{c.Hot}"));
        if (key == _key) return;
        _key = key;
        Children.Clear();

        // Back to front: rotation and offset of each card
        (double Angle, double X, double Y)[] poses = shown.Count switch
        {
            1 => [(-8, 0, 0)],
            2 => [(-2, 0.16, -0.12), (-11, -0.04, 0.04)],
            _ => [(4, 0.26, -0.16), (-3, 0.1, -0.06), (-12, -0.06, 0.06)],
        };
        var w = _cardWidth;
        var h = w / DeviceCard.Aspect;
        for (var back = shown.Count - 1; back >= 0; back--)
        {
            var (spec, state, hot) = shown[back];
            var pose = poses[shown.Count - 1 - back];
            var card = new DeviceCard(w);
            card.Update(spec, state, hot);
            card.RenderTransformOrigin = new Point(0.5, 0.5);
            card.RenderTransform = new RotateTransform { Angle = pose.Angle };
            SetLeft(card, (Width - w) / 2 + w * pose.X);
            SetTop(card, (Height - h) / 2 + w * pose.Y);
            Children.Add(card);
        }
    }
}
