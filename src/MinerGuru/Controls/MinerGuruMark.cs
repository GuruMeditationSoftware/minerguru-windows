using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Path = Microsoft.UI.Xaml.Shapes.Path;
using Windows.Foundation;

namespace MinerGuru.Controls;

/// <summary>
/// The app's mark: a square m made of wide bars. Its right leg stops short and runs into a small ASIC chip
/// with pins, in the accent color. Drawn on a grid of 24 units like the Mac app's MinerGuruMark.
/// </summary>
public sealed partial class MinerGuruMark : Canvas
{
    public MinerGuruMark(double size = 18)
    {
        Width = Height = size;
        var u = size / 24;

        var letter = new PathGeometry();
        letter.Figures.Add(Polyline(u, (3.8, 20.2), (3.8, 3.8), (20.2, 3.8), (20.2, 15)));
        letter.Figures.Add(Polyline(u, (12, 3.8), (12, 14)));
        Children.Add(new Path
        {
            Data = letter,
            Stroke = Palette.TextPrimary,
            StrokeThickness = size * 0.15,
            StrokeLineJoin = PenLineJoin.Miter,
            StrokeStartLineCap = PenLineCap.Flat,
            StrokeEndLineCap = PenLineCap.Flat,
        });

        var chip = new GeometryGroup();
        chip.Children.Add(new RectangleGeometry { Rect = new Rect(17.8 * u, 16.4 * u, 4.8 * u, 4.8 * u) });
        foreach (var (x, y, w, h) in new[] { (16.6, 17.45, 1.2, 0.7), (16.6, 19.45, 1.2, 0.7), (22.6, 17.45, 1.2, 0.7), (22.6, 19.45, 1.2, 0.7), (18.85, 21.2, 0.7, 1.2), (20.85, 21.2, 0.7, 1.2) })
            chip.Children.Add(new RectangleGeometry { Rect = new Rect(x * u, y * u, w * u, h * u) });
        Children.Add(new Path { Data = chip, Fill = Palette.Accent });
    }

    private static PathFigure Polyline(double u, params (double X, double Y)[] points)
    {
        var figure = new PathFigure { StartPoint = new Point(points[0].X * u, points[0].Y * u), IsClosed = false };
        foreach (var (x, y) in points.Skip(1)) figure.Segments.Add(new LineSegment { Point = new Point(x * u, y * u) });
        return figure;
    }
}
