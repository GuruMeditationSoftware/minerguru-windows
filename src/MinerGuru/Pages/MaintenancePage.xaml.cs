using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Shapes;
using MinerGuru.Controls;
using MinerGuru.Core.Models;
using MinerGuru.Core.Services;
using MinerGuru.Dialogs;
using static MinerGuru.Core.Lang;

namespace MinerGuru.Pages;

/// <summary>Planned maintenance with reminders, and the record of what was done.</summary>
public sealed partial class MaintenancePage : Page, IHeaderPage
{
    private static MaintenanceStore Maintenance => App.Store.Maintenance;

    private readonly PowerFigure _overdue = new(Strings.Overdue);
    private readonly PowerFigure _soon = new(Strings.InTheNext7Days);
    private readonly PowerFigure _planned = new(Strings.Planned);
    private readonly PowerFigure _done = new(Strings.Completed);
    private readonly StackPanel _taskRows = new();
    private readonly StackPanel _recordRows = new();

    public MaintenancePage()
    {
        InitializeComponent();
        var figures = new AdaptiveGrid { MinColumnWidth = 180, Spacing = 12 };
        foreach (var figure in new[] { _overdue, _soon, _planned, _done }) figures.Children.Add(figure);

        var add = new DropDownButton { Style = null, Content = Strings.AddTask, HorizontalAlignment = HorizontalAlignment.Right, Padding = new Thickness(10, 4, 8, 4) };
        var menu = new MenuFlyout();
        foreach (var kind in Enum.GetValues<MaintenanceKind>())
        {
            var item = new MenuFlyoutItem { Text = kind.Title() };
            item.Click += (_, _) => _ = EditAsync(new MaintenanceTask { Kind = kind, IntervalDays = kind.DefaultIntervalDays(), NextDue = DateTime.Today }, isNew: true);
            menu.Items.Add(item);
        }
        add.Flyout = menu;
        var header = new Grid();
        var title = PageParts.Title(Strings.Tasks);
        title.VerticalAlignment = VerticalAlignment.Center;
        header.Children.Add(title);
        header.Children.Add(add);
        var tasks = new StackPanel { Spacing = 8 };
        tasks.Children.Add(header);
        tasks.Children.Add(_taskRows);

        var records = new StackPanel { Spacing = 8 };
        records.Children.Add(PageParts.Title(Strings.Completed));
        records.Children.Add(_recordRows);

        var page = new StackPanel { Spacing = 16, MaxWidth = (double)Application.Current.Resources["PageMaxWidth"], Padding = (Thickness)Application.Current.Resources["PagePadding"] };
        page.Children.Add(figures);
        page.Children.Add(PageParts.Card(tasks));
        page.Children.Add(PageParts.Card(records));
        Content = new ScrollViewer { Content = page };

        Loaded += (_, _) =>
        {
            Maintenance.Changed += Render;
            Render();
        };
        Unloaded += (_, _) => Maintenance.Changed -= Render;
        ActualThemeChanged += (_, _) => Render();
    }

    public (string Title, string Subtitle) Header => (Strings.Maintenance, Strings.PlanTasksAndKeepTrack);

    private void Render()
    {
        var now = DateTime.Now;
        var tasks = Maintenance.Tasks.OrderBy(t => t.NextDue).ToList();
        _overdue.Update(tasks.Count(t => t.Status(now) == MaintenanceStatus.Overdue).ToString(), "");
        _soon.Update(tasks.Count(t => t.Status(now) == MaintenanceStatus.DueSoon).ToString(), "");
        _planned.Update(tasks.Count.ToString(), "");
        _done.Update(Maintenance.Records.Count.ToString(), "");

        _taskRows.Children.Clear();
        foreach (var task in tasks)
        {
            if (_taskRows.Children.Count > 0) _taskRows.Children.Add(new Rectangle { Height = 1, Fill = Palette.Hairline });
            _taskRows.Children.Add(TaskRow(task, now));
        }
        if (tasks.Count == 0)
            _taskRows.Children.Add(Palette.UiText(Strings.NoTasksYetYouWill, 13, Palette.TextSecondary));

        _recordRows.Children.Clear();
        foreach (var record in Maintenance.Records)
        {
            if (_recordRows.Children.Count > 0) _recordRows.Children.Add(new Rectangle { Height = 1, Fill = Palette.Hairline });
            _recordRows.Children.Add(RecordRow(record));
        }
        if (Maintenance.Records.Count == 0)
            _recordRows.Children.Add(Palette.UiText(Strings.NothingDoneYet, 13, Palette.TextSecondary));
    }

    private Grid TaskRow(MaintenanceTask task, DateTime now)
    {
        var row = PageParts.Row(1, 5);
        var days = task.DaysUntilDue(now);
        var status = task.Status(now);
        var color = status switch { MaintenanceStatus.Overdue => Palette.Bad, MaintenanceStatus.DueSoon => Palette.Warning, _ => Palette.Idle };
        row.Children.Add(new Ellipse { Width = 8, Height = 8, Fill = color, VerticalAlignment = VerticalAlignment.Center });

        var text = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        var title = Palette.UiText(task.Title, 13, Palette.TextPrimary);
        title.FontWeight = FontWeights.Medium;
        text.Children.Add(title);
        var when = days switch
        {
            < 0 => string.Format(Strings.OverdueByDays, -days),
            0 => Strings.DueToday,
            _ => string.Format(Strings.InDays, days),
        };
        var repeat = task.IntervalDays > 0 ? string.Format(Strings.EveryIntervalDaysDays, task.IntervalDays) : Strings.Once;
        var detail = Palette.MonoText($"{when} · {repeat} · {MinersText(task.MinerIds)}", 11, Palette.TextSecondary);
        detail.TextTrimming = TextTrimming.CharacterEllipsis;
        text.Children.Add(detail);
        if (task.Notes.Length > 0) text.Children.Add(Palette.UiText(task.Notes, 12, Palette.TextSecondary));
        row.Children.Add(text.At(1));

        row.Children.Add(PageParts.TextButton("", Strings.Completed, () => _ = MarkDoneAsync(task)).At(2));
        row.Children.Add(PageParts.IconButton("", Strings.Edit, () => _ = EditAsync(task, isNew: false)).At(3));
        row.Children.Add(PageParts.IconButton("", Strings.DeleteTask, () => Maintenance.Remove(task.Id)).At(4));
        return row;
    }

    private Grid RecordRow(MaintenanceRecord record)
    {
        var row = PageParts.Row(1, 3);
        var date = Palette.MonoText(record.DoneAt.ToString("d", Culture), 11, Palette.TextSecondary);
        date.VerticalAlignment = VerticalAlignment.Center;
        date.Width = 80;
        row.Children.Add(date);
        var text = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(Palette.UiText(record.Title, 13, Palette.TextPrimary));
        text.Children.Add(Palette.MonoText(MinersText(record.MinerIds), 11, Palette.TextSecondary));
        if (record.Notes.Length > 0) text.Children.Add(Palette.UiText(record.Notes, 12, Palette.TextSecondary));
        row.Children.Add(text.At(1));
        row.Children.Add(PageParts.IconButton("", Strings.DeleteEntry, () => Maintenance.RemoveRecord(record.Id)).At(2));
        return row;
    }

    private static string MinersText(HashSet<Guid> ids)
    {
        if (ids.Count == 0) return Strings.AllMiners;
        var names = App.Store.Miners.Where(m => ids.Contains(m.Id)).Select(m => m.DisplayName).ToList();
        return names.Count > 0 ? string.Join(", ", names) : Strings.MinerRemoved;
    }

    private async Task EditAsync(MaintenanceTask task, bool isNew)
    {
        var dialog = new MaintenanceTaskDialog(task, isNew, App.Store.Miners) { XamlRoot = XamlRoot, RequestedTheme = ActualTheme };
        if (await dialog.ShowAsync() == ContentDialogResult.Primary && dialog.Result is { } saved) Maintenance.Save(saved);
    }

    private async Task MarkDoneAsync(MaintenanceTask task)
    {
        var dialog = new MaintenanceDoneDialog(task) { XamlRoot = XamlRoot, RequestedTheme = ActualTheme };
        if (await dialog.ShowAsync() == ContentDialogResult.Primary) Maintenance.MarkDone(task, dialog.Date, dialog.Notes);
    }
}
