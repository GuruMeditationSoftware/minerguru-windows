using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using MinerGuru.Core.Models;
using static MinerGuru.Core.Lang;

namespace MinerGuru.Controls;

/// <summary>Seven day toggles, Monday first, with shortcuts for every day, workdays and the weekend. Edits the set it is given.</summary>
public sealed partial class WeekdayPicker : StackPanel
{
    private readonly HashSet<DayOfWeek> _days;
    private readonly Dictionary<DayOfWeek, ToggleButton> _toggles = [];

    public event Action? Changed;

    public WeekdayPicker(HashSet<DayOfWeek> days)
    {
        _days = days;
        Spacing = 6;
        var toggles = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        foreach (var day in WeekdayOrder.MondayFirst)
        {
            var toggle = new ToggleButton { Content = WeekdayOrder.ShortName(day), MinWidth = 42, Padding = new Thickness(6, 4, 6, 4), FontSize = 12, CornerRadius = new CornerRadius(6) };
            toggle.Click += (_, _) =>
            {
                if (toggle.IsChecked == true) _days.Add(day);
                else _days.Remove(day);
                Changed?.Invoke();
            };
            _toggles[day] = toggle;
            toggles.Children.Add(toggle);
        }
        Children.Add(toggles);

        var shortcuts = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
        shortcuts.Children.Add(Shortcut(L("Täglich", "Daily"), WeekdayOrder.All));
        shortcuts.Children.Add(Shortcut(WeekdayOrder.Summary(WeekdayOrder.Workdays), WeekdayOrder.Workdays));
        shortcuts.Children.Add(Shortcut(L("Wochenende", "Weekend"), WeekdayOrder.Weekend));
        Children.Add(shortcuts);
        Sync();
    }

    private HyperlinkButton Shortcut(string title, IReadOnlySet<DayOfWeek> days)
    {
        var link = new HyperlinkButton { Content = title, FontSize = 12, Padding = new Thickness(0) };
        link.Click += (_, _) =>
        {
            _days.Clear();
            _days.UnionWith(days);
            Sync();
            Changed?.Invoke();
        };
        return link;
    }

    private void Sync()
    {
        foreach (var (day, toggle) in _toggles) toggle.IsChecked = _days.Contains(day);
    }
}

/// <summary>From–to time of day in 15-minute steps; the same time twice means the whole day. Edits the window it is given.</summary>
public sealed partial class TimeWindowFields : StackPanel
{
    private const int Step = 15;

    private readonly WeeklyWindow _window;
    private readonly TextBlock _hint = Palette.UiText("", 12, Palette.TextSecondary);

    public TimeWindowFields(WeeklyWindow window)
    {
        _window = window;
        Spacing = 4;
        var fields = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        fields.Children.Add(Caption(L("Von", "From")));
        fields.Children.Add(Field(L("Von", "From"), window.StartMinute, minute => _window.StartMinute = minute));
        fields.Children.Add(Caption(L("bis", "to")));
        fields.Children.Add(Field(L("Bis", "To"), window.EndMinute, minute => _window.EndMinute = minute));
        Children.Add(fields);
        Children.Add(_hint);
        Sync();
    }

    private static TextBlock Caption(string text)
    {
        var caption = Palette.UiText(text, 13, Palette.TextSecondary);
        caption.VerticalAlignment = VerticalAlignment.Center;
        return caption;
    }

    private ComboBox Field(string name, int minute, Action<int> set)
    {
        var box = new ComboBox { Width = 104 };
        AutomationProperties.SetName(box, name);
        for (var m = 0; m < WeeklyWindow.MinutesPerDay; m += Step) box.Items.Add(WeeklyWindow.Clock(m));
        box.SelectedIndex = minute / Step;
        box.SelectionChanged += (_, _) =>
        {
            set(box.SelectedIndex * Step);
            Sync();
        };
        return box;
    }

    private void Sync() => _hint.Text = _window.IsWholeDay
        ? L("Gleiche Zeit zweimal: gilt den ganzen Tag.", "Same time twice: applies all day.")
        : _window.WrapsMidnight ? L("Läuft über Mitternacht in den nächsten Tag.", "Runs past midnight into the next day.") : " ";
}

/// <summary>Label and control side by side, as the Mac's column form.</summary>
public static class PowerForm
{
    public static Grid Row(string label, FrameworkElement control)
    {
        var row = new Grid { ColumnSpacing = 12 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(96) });
        row.ColumnDefinitions.Add(new ColumnDefinition());
        var caption = Palette.UiText(label, 13, Palette.TextSecondary);
        caption.TextAlignment = TextAlignment.Right;
        caption.Margin = new Thickness(0, 6, 0, 0);
        row.Children.Add(caption);
        Grid.SetColumn(control, 1);
        row.Children.Add(control);
        return row;
    }

    public static TextBlock Note(string text)
    {
        var note = Palette.UiText(text, 12, Palette.TextSecondary);
        note.TextWrapping = TextWrapping.Wrap;
        return note;
    }
}
