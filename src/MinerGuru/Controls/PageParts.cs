using Microsoft.UI.Text;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Windows.Storage.Pickers;
using static MinerGuru.Core.Lang;

namespace MinerGuru.Controls;

/// <summary>Small pieces the code-built pages share.</summary>
internal static class PageParts
{
    public static Style Resource(string key) => (Style)Application.Current.Resources[key];

    public static Border Card(UIElement child) => new() { Child = child, Padding = new Thickness(16), Style = Resource("CardBorderStyle") };

    public static TextBlock Title(string text)
    {
        var title = Palette.UiText(text, 15, Palette.TextPrimary);
        title.FontWeight = FontWeights.Medium;
        return title;
    }

    public static Button IconButton(string glyph, string tip, Action click)
    {
        var button = new Button
        {
            Content = new FontIcon { Glyph = glyph, FontSize = 12 },
            Style = Resource("SmallButtonStyle"),
            VerticalAlignment = VerticalAlignment.Center,
        };
        ToolTipService.SetToolTip(button, tip);
        AutomationProperties.SetName(button, tip);
        button.Click += (_, _) => click();
        return button;
    }

    public static Button TextButton(string glyph, string text, Action click, bool accent = false)
    {
        var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        content.Children.Add(new FontIcon { FontSize = 12, Glyph = glyph });
        content.Children.Add(new TextBlock { Text = text });
        var button = new Button { Content = content, Style = Resource(accent ? "SmallAccentButtonStyle" : "SmallButtonStyle") };
        AutomationProperties.SetName(button, text);
        button.Click += (_, _) => click();
        return button;
    }

    /// <summary>A grid with a stretching column at <paramref name="stretch"/> and auto columns around it.</summary>
    public static Grid Row(int stretch, int columns)
    {
        var grid = new Grid { ColumnSpacing = 10, Padding = new Thickness(0, 6, 0, 6) };
        for (var i = 0; i < columns; i++)
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = i == stretch ? new GridLength(1, GridUnitType.Star) : GridLength.Auto });
        return grid;
    }

    public static T At<T>(this T element, int column) where T : FrameworkElement
    {
        Grid.SetColumn(element, column);
        return element;
    }

    /// <summary>Asks where to save and writes the file; <c>false</c> when the user cancelled.</summary>
    public static async Task<bool> SaveFileAsync(string fileName, string typeName, string extension, byte[] content)
    {
        var picker = new FileSavePicker(App.MainWindow.AppWindow.Id) { SuggestedFileName = fileName, DefaultFileExtension = extension };
        picker.FileTypeChoices.Add(typeName, new List<string> { extension });
        if (await picker.PickSaveFileAsync() is not { } result) return false;
        await File.WriteAllBytesAsync(result.Path, content);
        return true;
    }
}

/// <summary>Who a rule or task is for, as the dialogs' "All miners" box with checkboxes. Edits the set it is given; empty means all.</summary>
internal sealed partial class MinerScopePicker : StackPanel
{
    private readonly HashSet<Guid> _ids;
    private readonly IReadOnlyList<Core.Models.MinerDevice> _miners;
    private readonly CheckBox _all = new() { Content = L("Alle Miner", "All miners") };
    private readonly StackPanel _checks = new();

    public MinerScopePicker(HashSet<Guid> ids, IReadOnlyList<Core.Models.MinerDevice> miners)
    {
        _ids = ids;
        _miners = miners;
        _all.Click += (_, _) =>
        {
            _ids.Clear();
            if (_all.IsChecked != true) _ids.UnionWith(_miners.Select(m => m.Id));
            Render();
        };
        Children.Add(_all);
        Children.Add(new ScrollViewer { Content = _checks, MaxHeight = 150 });
        Render();
    }

    private void Render()
    {
        _all.IsChecked = _ids.Count == 0;
        _checks.Children.Clear();
        if (_ids.Count == 0) return;
        foreach (var miner in _miners)
        {
            var check = new CheckBox { Content = miner.DisplayName, IsChecked = _ids.Contains(miner.Id) };
            var id = miner.Id;
            check.Click += (_, _) =>
            {
                if (check.IsChecked == true) _ids.Add(id);
                else if (_ids.Count > 1) _ids.Remove(id);
                else check.IsChecked = true;    // one stays: an empty set would mean all miners
            };
            _checks.Children.Add(check);
        }
    }
}
