using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Shapes;
using MinerGuru.Core;
using MinerGuru.Core.Models;
using MinerGuru.Core.Services;
using static MinerGuru.Core.Lang;

namespace MinerGuru.Controls;

/// <summary>The draft pool settings of one miner on the pools page, with what loaded them and whether they are being sent.</summary>
public sealed class PoolDraft(MinerDevice miner)
{
    public MinerDevice Miner { get; set; } = miner;
    public PoolSettingsForm Baseline { get; set; } = new(miner);
    public PoolSettingsForm Form { get; set; } = new PoolSettingsForm(miner);
    public bool IsApplying { get; set; }
    public string? Error { get; set; }
    public bool IsDirty => !Form.HasSamePoolSettings(Baseline);
}

/// <summary>
/// Pool slots of one miner, as the Mac's MinerPoolAssignmentCard: a library pool per slot, primary and fallback
/// slot (AxeOS 2.15+), failover or dual pool with the split (NerdOS), and Apply &amp; Restart.
/// </summary>
public sealed partial class PoolAssignmentCard : Grid
{
    private readonly Border _border = new() { CornerRadius = new CornerRadius(14), BorderThickness = new Thickness(1), Padding = new Thickness(16) };
    private readonly PoolDraft _draft;
    private readonly PoolLibrary _library;
    private readonly StatusBlock _status = new();
    /// <summary>Set while the controls are rebuilt, so their initial values do not count as edits.</summary>
    private bool _rendering;
    /// <summary>The input that gets the focus back after a rebuild; without it the focus falls to the first sidebar row.</summary>
    private string? _refocus;

    public PoolAssignmentCard(PoolDraft draft, PoolLibrary library)
    {
        _draft = draft;
        _library = library;
        Children.Add(_border);
        ActualThemeChanged += (_, _) => Render();
        Render();
    }

    /// <summary>The draft changed by the user (the page recounts "Apply all").</summary>
    public event Action? Edited;
    public event Func<Task>? ApplyRequested;
    public event Action? ResetRequested;
    public event Action<PoolEndpoint>? SaveToLibraryRequested;

    private PoolSettingsForm Form => _draft.Form;
    private bool IsDual => _draft.Miner.SupportsDualPool && Form.Mode == PoolMode.Dual;

    /// <summary>Only the status square; a full <see cref="Render"/> would close an open drop-down.</summary>
    public void UpdateStatus() => _status.Apply(_draft.Miner, App.Store.IsHot(_draft.Miner));

    public void Render()
    {
        _rendering = true;
        _border.Background = Palette.Get("CardBrush");
        _border.BorderBrush = _draft.IsDirty ? Palette.Accent : Palette.Hairline;

        var stack = new StackPanel { Spacing = 12 };
        stack.Children.Add(Header());
        if (_draft.Miner.SupportsDualPool) stack.Children.Add(DualPoolSection());
        var slots = new StackPanel { Spacing = 6 };
        foreach (var index in Form.VisibleSlots) slots.Children.Add(SlotRow(index));
        stack.Children.Add(slots);
        if (Form.Api == PoolApi.Slots) stack.Children.Add(RoleSection());
        stack.Children.Add(Footer());
        _border.Child = stack;
        _rendering = false;
    }

    private void Changed(string input)
    {
        if (_rendering) return;
        _draft.Error = null;
        _refocus = input;
        Render();
        _refocus = null;
        Edited?.Invoke();
    }

    /// <summary>Names a rebuilt input, so it can take the focus back once it is in the tree.</summary>
    private T Input<T>(string name, T control) where T : Control
    {
        if (name != _refocus) return control;
        void Restore(object sender, RoutedEventArgs e)
        {
            control.Loaded -= Restore;
            control.Focus(FocusState.Programmatic);
        }
        control.Loaded += Restore;
        return control;
    }

    // Sections

    private Grid Header()
    {
        var miner = _draft.Miner;
        // The status square is kept across renders; it can only sit in one header at a time.
        (_status.Parent as Panel)?.Children.Remove(_status);
        UpdateStatus();
        _status.VerticalAlignment = VerticalAlignment.Center;
        var name = Palette.UiText(miner.DisplayName, 15);
        name.FontWeight = Microsoft.UI.Text.FontWeights.Medium;
        var detail = Form.Api == PoolApi.Slots
            ? L($"{AxeOsPool.SlotCount} Pool-Slots", $"{AxeOsPool.SlotCount} pool slots")
            : miner.SupportsDualPool ? L("Failover oder Dual-Pool", "Failover or dual pool") : L("Primär + Fallback", "Primary + fallback");
        var names = new StackPanel { Spacing = 2 };
        names.Children.Add(name);
        names.Children.Add(Palette.UiText($"{MinerGuru.Core.Privacy.Ip(miner.IpAddress)} • {detail}", 12, Palette.TextSecondary));
        var hardware = Palette.MonoText(miner.HardwareTitle, 11, Palette.TextSecondary);
        hardware.VerticalAlignment = VerticalAlignment.Center;

        var header = new Grid { ColumnSpacing = 8 };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition());
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(names, 1);
        Grid.SetColumn(hardware, 2);
        header.Children.Add(_status);
        header.Children.Add(names);
        header.Children.Add(hardware);
        return header;
    }

    private StackPanel DualPoolSection()
    {
        var group = $"mode-{_draft.Miner.Id}";
        var failover = Input("failover", new RadioButton { Content = "Failover", GroupName = group, IsChecked = Form.Mode == PoolMode.Failover, MinWidth = 0 });
        var dual = Input("dual", new RadioButton { Content = "Dual-Pool", GroupName = group, IsChecked = Form.Mode == PoolMode.Dual, MinWidth = 0 });
        failover.Checked += (_, _) => { Form.Mode = PoolMode.Failover; Changed("failover"); };
        dual.Checked += (_, _) => { Form.Mode = PoolMode.Dual; Changed("dual"); };
        var modes = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 16 };
        modes.Children.Add(failover);
        modes.Children.Add(dual);

        var section = new StackPanel { Spacing = 4 };
        section.Children.Add(modes);
        if (IsDual)
        {
            var p1 = Palette.MonoText($"P1 {Form.PrimaryPercent}%", 11, Palette.TextPrimary);
            var p2 = Palette.MonoText($"P2 {Form.SecondaryPercent}%", 11, Palette.TextPrimary);
            var slider = Input("split", new Slider { Minimum = 1, Maximum = 99, StepFrequency = 1, Value = Form.PrimaryPercent, VerticalAlignment = VerticalAlignment.Center });
            slider.ValueChanged += (_, e) =>
            {
                if (_rendering) return;
                Form.Balance = (int)e.NewValue;
                p1.Text = $"P1 {Form.PrimaryPercent}%";
                p2.Text = $"P2 {Form.SecondaryPercent}%";
            };
            // Re-render (dirty frame, role badges) once the thumb is let go, not on every step.
            slider.PointerCaptureLost += (_, _) => Changed("split");
            slider.KeyUp += (_, _) => Changed("split");
            var row = new Grid { ColumnSpacing = 8 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition());
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            p1.VerticalAlignment = p2.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(slider, 1);
            Grid.SetColumn(p2, 2);
            row.Children.Add(p1);
            row.Children.Add(slider);
            row.Children.Add(p2);
            section.Children.Add(row);
        }
        return section;
    }

    private Grid SlotRow(int index)
    {
        var endpoint = Form.Slots[index];
        var row = new Grid { ColumnSpacing = 8 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(64) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(56) });
        row.ColumnDefinitions.Add(new ColumnDefinition());
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(74) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(32) });

        var label = Palette.UiText(SlotLabel(index), 12, Palette.TextPrimary);
        label.FontWeight = Microsoft.UI.Text.FontWeights.Medium;
        label.VerticalAlignment = VerticalAlignment.Center;
        row.Children.Add(label);

        if (endpoint.HasContent)
        {
            var coin = Badges.Coin(CoinFor(index));
            Grid.SetColumn(coin, 1);
            row.Children.Add(coin);
        }

        var picker = PoolPicker(index, endpoint);
        Grid.SetColumn(picker, 2);
        row.Children.Add(picker);

        if (RoleBadge(index) is { } role)
        {
            role.HorizontalAlignment = HorizontalAlignment.Right;
            Grid.SetColumn(role, 3);
            row.Children.Add(role);
        }

        if (endpoint.HasContent && _library.Matching(endpoint) == null)
        {
            var save = new Button
            {
                Content = new FontIcon { Glyph = "", FontSize = 12 },
                Style = (Style)Application.Current.Resources["BarButtonStyle"],
            };
            ToolTipService.SetToolTip(save, L("Diesen Pool in die Bibliothek übernehmen", "Add this pool to the library"));
            AutomationProperties.SetName(save, L("Diesen Pool in die Bibliothek übernehmen", "Add this pool to the library"));
            save.Click += (_, _) => SaveToLibraryRequested?.Invoke(Form.Slots[index]);
            Grid.SetColumn(save, 4);
            row.Children.Add(save);
        }
        return row;
    }

    /// <summary>"— empty —", the slot's pool if it is not in the library, then every library pool.</summary>
    private ComboBox PoolPicker(int index, PoolEndpoint endpoint)
    {
        var picker = Input($"slot{index}", new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch, FontSize = 12, MinHeight = 28, Padding = new Thickness(10, 3, 0, 4) });
        AutomationProperties.SetName(picker, Form.SlotTitle(index));
        var choices = new List<PoolConfig?> { null };
        picker.Items.Add(L("— leer —", "— empty —"));
        var match = _library.Matching(endpoint);
        var unsaved = endpoint.HasContent && match == null;
        if (unsaved)
        {
            choices.Add(null);
            picker.Items.Add(L($"{StratumHost.Clean(endpoint.Host)}:{endpoint.Port} (nicht in Bibliothek)", $"{StratumHost.Clean(endpoint.Host)}:{endpoint.Port} (not in library)"));
        }
        foreach (var pool in _library.Pools)
        {
            choices.Add(pool);
            picker.Items.Add($"{pool.Name} – {pool.HostAndPort}");
        }
        picker.SelectedIndex = !endpoint.HasContent ? 0 : unsaved ? 1 : choices.IndexOf(match);
        picker.SelectionChanged += (_, _) =>
        {
            if (_rendering || picker.SelectedIndex < 0) return;
            if (picker.SelectedIndex == 0) Form.ClearSlot(index);
            else if (choices[picker.SelectedIndex] is { } config) Form.Assign(config, index);
            else return; // the unsaved pool that is already there
            Changed($"slot{index}");
        };
        return picker;
    }

    /// <summary>Coin from pool host and wallet; the network difficulty helps while the slot holds the connected pool.</summary>
    private MinedCoin CoinFor(int index)
    {
        var endpoint = Form.Slots[index];
        var host = StratumHost.Clean(endpoint.Host);
        double? difficulty = null;
        if (_draft.Miner.LatestInfo is { } info)
        {
            if (index == Form.PrimarySlot && string.Equals(host, StratumHost.Clean(info.StratumUrl), StringComparison.OrdinalIgnoreCase))
                difficulty = info.PrimaryPoolStatus?.NetworkDifficulty;
            else if (index == Form.SecondarySlot && string.Equals(host, StratumHost.Clean(info.FallbackStratumUrl), StringComparison.OrdinalIgnoreCase))
                difficulty = info.SecondaryPoolStatus?.NetworkDifficulty;
        }
        return MinedCoinDetector.Detect(endpoint.Host, endpoint.User, difficulty);
    }

    private string SlotLabel(int index) =>
        Form.Api == PoolApi.Slots ? Form.SlotTitle(index)
        : index == 0 ? (IsDual ? "Pool 1" : L("Primär", "Primary"))
        : (IsDual ? "Pool 2" : "Fallback");

    private StackPanel? RoleBadge(int index)
    {
        if (Form.Api == PoolApi.Slots)
        {
            if (index == Form.PrimarySlot) return Role(L("Primär", "Primary"), Palette.Good);
            if (index == Form.SecondarySlot) return Role("Fallback", Palette.Warning);
            if (Form.Slots[index].HasContent) return Role("Reserve", Palette.TextSecondary);
            return null;
        }
        return IsDual ? Role($"{(index == 0 ? Form.PrimaryPercent : Form.SecondaryPercent)}%", Palette.Idle) : null;
    }

    private static StackPanel Role(string text, Microsoft.UI.Xaml.Media.Brush color)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
        row.Children.Add(new Ellipse { Width = 6, Height = 6, Fill = color, VerticalAlignment = VerticalAlignment.Center });
        row.Children.Add(Palette.UiText(text, 11, Palette.TextSecondary));
        return row;
    }

    /// <summary>Primary and fallback slot (AxeOS 2.15+): filled slots and the current one.</summary>
    private Grid RoleSection()
    {
        var grid = new Grid { ColumnSpacing = 12 };
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        var primary = RolePicker(L("Primär", "Primary"), Form.PrimarySlot, i => Form.PrimarySlot = i);
        var fallback = RolePicker("Fallback", Form.SecondarySlot, i => Form.SecondarySlot = i);
        Grid.SetColumn(fallback, 1);
        grid.Children.Add(primary);
        grid.Children.Add(fallback);
        return grid;
    }

    private ComboBox RolePicker(string header, int current, Action<int> set)
    {
        var choices = Enumerable.Range(0, Form.Slots.Count).Where(i => Form.Slots[i].HasContent || i == current).ToList();
        var picker = Input(header, new ComboBox { Header = header, HorizontalAlignment = HorizontalAlignment.Stretch, FontSize = 12 });
        foreach (var i in choices)
        {
            var slot = Form.Slots[i];
            picker.Items.Add(slot.HasContent
                ? $"{Form.SlotTitle(i)}: {_library.Matching(slot)?.Name ?? StratumHost.Clean(slot.Host)}"
                : L($"{Form.SlotTitle(i)} (leer)", $"{Form.SlotTitle(i)} (empty)"));
        }
        picker.SelectedIndex = choices.IndexOf(current);
        picker.SelectionChanged += (_, _) =>
        {
            if (_rendering || picker.SelectedIndex < 0) return;
            set(choices[picker.SelectedIndex]);
            Changed(header);
        };
        return picker;
    }

    private StackPanel Footer()
    {
        var footer = new StackPanel { Spacing = 6 };
        if (_draft.IsDirty && Form.ValidationMessage is { } message) footer.Children.Add(Notice("", message, Palette.Warning));
        if (_draft.IsDirty && Form.CoinWarning is { } coin) footer.Children.Add(Notice("", coin, Palette.Warning));
        if (_draft.Error is { } error) footer.Children.Add(Notice("", error, Palette.Bad));

        var bar = new Grid { ColumnSpacing = 8 };
        bar.ColumnDefinitions.Add(new ColumnDefinition());
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        if (Form.SlotsToClear.Count > 0)
        {
            var clearing = Palette.UiText(L("Wird geleert: ", "Will be cleared: ") + string.Join(", ", Form.SlotsToClear.Select(Form.SlotTitle)), 11, Palette.TextSecondary);
            clearing.VerticalAlignment = VerticalAlignment.Center;
            bar.Children.Add(clearing);
        }

        var reset = new Button
        {
            Content = L("Zurücksetzen", "Reset"),
            Style = (Style)Application.Current.Resources["SmallButtonStyle"],
            IsEnabled = _draft.IsDirty && !_draft.IsApplying,
        };
        reset.Click += (_, _) => ResetRequested?.Invoke();
        Grid.SetColumn(reset, 1);
        bar.Children.Add(reset);

        var applyContent = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        if (_draft.IsApplying) applyContent.Children.Add(new ProgressRing { IsActive = true, Width = 12, Height = 12 });
        applyContent.Children.Add(new TextBlock { Text = L("Übernehmen & neu starten", "Apply & restart") });
        var apply = new Button
        {
            Content = applyContent,
            Style = (Style)Application.Current.Resources["SmallAccentButtonStyle"],
            IsEnabled = _draft.IsDirty && !_draft.IsApplying && Form.ValidationMessage == null && _draft.Miner.IsOnline,
        };
        ToolTipService.SetToolTip(apply, L("Pools an den Miner senden; der Miner startet danach neu, damit sie aktiv werden",
                                           "Send the pools to the miner; it restarts afterwards so they take effect"));
        AutomationProperties.SetName(apply, L("Übernehmen & neu starten", "Apply & restart"));
        apply.Click += async (_, _) => { if (ApplyRequested != null) await ApplyRequested.Invoke(); };
        Grid.SetColumn(apply, 2);
        bar.Children.Add(apply);
        footer.Children.Add(bar);
        return footer;
    }

    private static StackPanel Notice(string glyph, string text, Microsoft.UI.Xaml.Media.Brush color)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        row.Children.Add(new FontIcon { Glyph = glyph, FontSize = 12, Foreground = color });
        var message = Palette.UiText(text, 12, color);
        message.TextWrapping = TextWrapping.Wrap;
        row.Children.Add(message);
        return row;
    }
}
