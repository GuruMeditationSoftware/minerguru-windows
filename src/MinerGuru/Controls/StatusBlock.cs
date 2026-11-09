using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using MinerGuru.Core.Models;
using static MinerGuru.Core.Lang;

namespace MinerGuru.Controls;

/// <summary>
/// The signature detail: a small square LED that shows a miner's state. Each state has its own shape, so it reads
/// without telling the colors apart: online a filled block, too hot a filled diamond, checking an outline, offline an outline with a slash.
/// </summary>
public sealed partial class StatusBlock : Canvas
{
    private enum Look { Online, Hot, Checking, Offline }

    private readonly double _size;

    public StatusBlock(double size = 8)
    {
        _size = size;
        Width = Height = size;
        IsHitTestVisible = true;
    }

    public void Apply(ConnectionState state, bool hot, string? offlineReason = null)
    {
        var look = state switch
        {
            ConnectionState.Online => hot ? Look.Hot : Look.Online,
            ConnectionState.Offline => Look.Offline,
            _ => Look.Checking,
        };
        var brush = look switch
        {
            Look.Online => Palette.Good,
            Look.Hot => Palette.Warning,
            Look.Checking => Palette.Idle,
            _ => Palette.Bad,
        };
        var s = _size;
        var stroke = Math.Max(1, s * 0.16);
        Children.Clear();
        switch (look)
        {
            case Look.Online:
                Children.Add(new Rectangle { Width = s, Height = s, Fill = brush });
                break;
            case Look.Hot:
                var side = s * 0.78;
                var diamond = new Rectangle { Width = side, Height = side, Fill = brush, RenderTransformOrigin = new(0.5, 0.5), RenderTransform = new RotateTransform { Angle = 45 } };
                SetLeft(diamond, (s - side) / 2);
                SetTop(diamond, (s - side) / 2);
                Children.Add(diamond);
                break;
            default:
                Children.Add(new Rectangle { Width = s, Height = s, Stroke = brush, StrokeThickness = stroke });
                if (look == Look.Offline)
                    Children.Add(new Line { X1 = 0, Y1 = s, X2 = s, Y2 = 0, Stroke = brush, StrokeThickness = stroke });
                break;
        }
        ToolTipService.SetToolTip(this, look switch
        {
            Look.Online => "Online",
            Look.Hot => L("Online, Temperatur hoch", "Online, temperature high"),
            Look.Checking => L("Verbindung wird geprüft", "Checking the connection"),
            _ => offlineReason is { } reason ? $"Offline: {reason}" : "Offline",
        });
    }

    public void Apply(MinerDevice miner, bool hot) => Apply(miner.State, hot, miner.OfflineReason);
}
