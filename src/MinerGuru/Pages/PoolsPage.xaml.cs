using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Shapes;
using MinerGuru.Controls;
using MinerGuru.Core.Models;
using MinerGuru.Core.Services;
using MinerGuru.Dialogs;
using static MinerGuru.Core.Lang;

namespace MinerGuru.Pages;

public sealed partial class PoolsPage : Page, IHeaderPage
{
    private static PoolLibrary Library => App.Store.PoolLibrary;

    /// <summary>Unsaved pool settings per miner. Kept for the app's lifetime, so leaving the page loses nothing.</summary>
    private static readonly Dictionary<Guid, PoolDraft> Drafts = [];
    /// <summary>The first-visit import ran and found pools; later an empty library stays empty.</summary>
    private static bool _imported;
    private readonly Dictionary<Guid, PoolAssignmentCard> _cards = [];
    private readonly Dictionary<Guid, TextBlock> _usageTexts = [];

    public PoolsPage()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            App.Store.Changed += SyncDrafts;
            Library.Changed += OnLibraryChanged;
            RenderLibrary();
            SyncDrafts();
        };
        Unloaded += (_, _) =>
        {
            App.Store.Changed -= SyncDrafts;
            Library.Changed -= OnLibraryChanged;
        };
        ActualThemeChanged += (_, _) => { RenderLibrary(); RenderUsage(); };
    }

    public (string Title, string Subtitle) Header =>
        (L("Pool-Konfiguration", "Pool configuration"), L("Pools speichern und Minern zuordnen", "Save pools and assign them to miners"));

    private static IEnumerable<PoolDraft> ConfigurableDrafts =>
        App.Store.Miners.Where(m => Drafts.ContainsKey(m.Id)).Select(m => Drafts[m.Id]);

    // Drafts

    /// <summary>Picks up new telemetry for miners without unsaved changes; drops removed miners.</summary>
    private void SyncDrafts()
    {
        var store = App.Store;
        // First visit: fill the library with the pools already on the miners, once they have reported.
        if (!_imported && Library.Pools.Count == 0) _imported = Library.Import(store.Miners) > 0;
        var ids = store.Miners.Select(m => m.Id).ToHashSet();
        foreach (var gone in Drafts.Keys.Where(id => !ids.Contains(id)).ToList()) Drafts.Remove(gone);
        foreach (var gone in _cards.Keys.Where(id => !ids.Contains(id)).ToList()) _cards.Remove(gone);

        foreach (var miner in store.Miners)
        {
            if (miner.LatestInfo == null) continue;
            if (!Drafts.TryGetValue(miner.Id, out var draft))
            {
                Drafts[miner.Id] = new PoolDraft(miner);
                continue;
            }
            draft.Miner = miner;
            if (draft.IsDirty || draft.IsApplying) continue;
            if (draft.Baseline.HasSamePoolSettings(new PoolSettingsForm(miner))) continue;
            Reload(draft);
        }
        SyncCards();
    }

    private void Reload(PoolDraft draft)
    {
        draft.Baseline = new PoolSettingsForm(draft.Miner);
        draft.Form = draft.Baseline.Clone();
        draft.Error = null;
        if (_cards.TryGetValue(draft.Miner.Id, out var card)) card.Render();
    }

    private void SyncCards()
    {
        var drafts = ConfigurableDrafts.ToList();
        NoMiners.Visibility = drafts.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        var cards = new List<PoolAssignmentCard>();
        foreach (var draft in drafts)
        {
            if (!_cards.TryGetValue(draft.Miner.Id, out var card))
            {
                card = new PoolAssignmentCard(draft, Library);
                card.Edited += UpdateToolbar;
                card.ApplyRequested += () => ApplyAsync(draft);
                card.ResetRequested += () => { Reload(draft); UpdateToolbar(); };
                card.SaveToLibraryRequested += endpoint => _ = EditAsync(PoolConfig.From(endpoint), isNew: true);
                _cards[draft.Miner.Id] = card;
            }
            else card.UpdateStatus();
            cards.Add(card);
        }
        if (!CardGrid.Children.Cast<PoolAssignmentCard>().SequenceEqual(cards))
        {
            CardGrid.Children.Clear();
            foreach (var card in cards) CardGrid.Children.Add(card);
        }
        UpdateToolbar();
        RenderUsage();
    }

    private void UpdateToolbar()
    {
        var dirty = ConfigurableDrafts.Where(d => d.IsDirty).ToList();
        ApplyAllText.Text = L($"Alle übernehmen ({dirty.Count})", $"Apply all ({dirty.Count})");
        ApplyAllButton.IsEnabled = dirty.Count > 0 && dirty.All(d => d.Form.ValidationMessage == null && !d.IsApplying);
    }

    private async Task ApplyAsync(PoolDraft draft)
    {
        if (draft.Form.ValidationMessage != null || draft.IsApplying) return;
        draft.IsApplying = true;
        draft.Error = null;
        Rerender(draft);
        try
        {
            await App.Store.ApplyPoolSettingsAsync(draft.Form.Clone(), draft.Miner.Id);
            draft.IsApplying = false;
            if (App.Store.Find(draft.Miner.Id) is { } miner) draft.Miner = miner;
            Reload(draft);
        }
        catch (WriteDeclinedException)
        {
            // Declined in the confirmation: the draft stays as it is.
            draft.IsApplying = false;
        }
        catch (MinerApiException e)
        {
            draft.IsApplying = false;
            draft.Error = e.Message;
        }
        Rerender(draft);
    }

    private void Rerender(PoolDraft draft)
    {
        if (_cards.TryGetValue(draft.Miner.Id, out var card)) card.Render();
        UpdateToolbar();
    }

    private async void ApplyAll_Click(object sender, RoutedEventArgs e) =>
        await Task.WhenAll(ConfigurableDrafts.Where(d => d.IsDirty).ToList().Select(ApplyAsync));

    // Library

    private void OnLibraryChanged()
    {
        RenderLibrary();
        RenderUsage();
        // The slot pickers list the library.
        foreach (var card in _cards.Values) card.Render();
    }

    private void RenderLibrary()
    {
        LibraryCount.Text = Library.Pools.Count.ToString();
        LibraryEmpty.Visibility = Library.Pools.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        LibraryRows.Children.Clear();
        _usageTexts.Clear();
        for (var i = 0; i < Library.Pools.Count; i++)
        {
            if (i > 0) LibraryRows.Children.Add(new Rectangle { Height = 1, Fill = Palette.Hairline });
            LibraryRows.Children.Add(LibraryRow(Library.Pools[i]));
        }
    }

    /// <summary>How many miners use each pool, as loaded from the miners (not the drafts).</summary>
    private void RenderUsage()
    {
        foreach (var (id, text) in _usageTexts)
        {
            if (Library.Find(id) is not { } pool) continue;
            var usage = ConfigurableDrafts.Count(d => d.Baseline.Slots.Any(pool.Matches));
            text.Text = usage == 0 ? "" : usage == 1 ? L("1 Miner", "1 miner") : L($"{usage} Miner", $"{usage} miners");
        }
    }

    private Grid LibraryRow(PoolConfig pool)
    {
        var row = new Grid { ColumnSpacing = 10, Padding = new Thickness(0, 6, 0, 6) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(56) });
        row.ColumnDefinitions.Add(new ColumnDefinition());
        for (var i = 0; i < 5; i++) row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        row.Children.Add(Badges.Coin(MinedCoinDetector.Detect(pool.Host, pool.User)));

        var name = Palette.UiText(pool.Name, 13);
        name.FontWeight = Microsoft.UI.Text.FontWeights.Medium;
        var detail = Palette.MonoText(pool.User.Length == 0 ? pool.HostAndPort : $"{pool.HostAndPort} • {pool.User}", 11, Palette.TextSecondary);
        var texts = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        texts.Children.Add(name);
        texts.Children.Add(detail);
        Grid.SetColumn(texts, 1);
        row.Children.Add(texts);

        var usage = Palette.UiText("", 11, Palette.TextSecondary);
        usage.FontWeight = Microsoft.UI.Text.FontWeights.Medium;
        usage.VerticalAlignment = VerticalAlignment.Center;
        _usageTexts[pool.Id] = usage;
        Grid.SetColumn(usage, 2);
        row.Children.Add(usage);

        var assignMenu = new MenuFlyout { Placement = Microsoft.UI.Xaml.Controls.Primitives.FlyoutPlacementMode.BottomEdgeAlignedRight };
        var asPrimary = new MenuFlyoutItem { Text = L("Als Primär-Pool auf allen Minern", "As primary pool on all miners") };
        asPrimary.Click += (_, _) => AssignEverywhere(pool, asPrimary: true);
        var asFallback = new MenuFlyoutItem { Text = L("Als Fallback / Pool 2 auf allen Minern", "As fallback / pool 2 on all miners") };
        asFallback.Click += (_, _) => AssignEverywhere(pool, asPrimary: false);
        assignMenu.Items.Add(asPrimary);
        assignMenu.Items.Add(asFallback);
        var assign = new DropDownButton
        {
            Content = L("Zuweisen", "Assign"),
            Flyout = assignMenu,
            FontSize = 12,
            MinHeight = 28,
            Padding = new Thickness(10, 4, 8, 4),
            CornerRadius = new CornerRadius(6),
            VerticalAlignment = VerticalAlignment.Center,
        };
        AutomationProperties.SetName(assign, L($"{pool.Name} zuweisen", $"Assign {pool.Name}"));
        ToolTipService.SetToolTip(assign, L("Pool in die Entwürfe aller Miner übernehmen – gesendet wird erst mit „Übernehmen & neu starten“",
                                            "Put the pool into the drafts of all miners – nothing is sent until “Apply & restart”"));
        Grid.SetColumn(assign, 3);
        row.Children.Add(assign);

        AddIconButton(row, 4, "", L("Bearbeiten", "Edit"), () => _ = EditAsync(pool.Copy(), isNew: false));
        AddIconButton(row, 5, "", L("Duplizieren (z. B. für einen anderen Worker)", "Duplicate (e.g. for another worker)"), () =>
        {
            if (Library.Duplicate(pool.Id) is { } copy) _ = EditAsync(copy.Copy(), isNew: false);
        });
        AddIconButton(row, 6, "", L("Aus der Bibliothek löschen", "Delete from library"), () => _ = ConfirmDeleteAsync(pool));
        return row;
    }

    private static void AddIconButton(Grid row, int column, string glyph, string tip, Action click)
    {
        var button = new Button
        {
            Content = new FontIcon { Glyph = glyph, FontSize = 12 },
            Style = (Style)Application.Current.Resources["BarButtonStyle"],
            VerticalAlignment = VerticalAlignment.Center,
        };
        ToolTipService.SetToolTip(button, tip);
        AutomationProperties.SetName(button, tip);
        button.Click += (_, _) => click();
        Grid.SetColumn(button, column);
        row.Children.Add(button);
    }

    private void AssignEverywhere(PoolConfig pool, bool asPrimary)
    {
        foreach (var draft in ConfigurableDrafts)
        {
            if (asPrimary) draft.Form.MakePrimary(pool);
            else draft.Form.MakeFallback(pool);
            draft.Error = null;
            Rerender(draft);
        }
    }

    private void Import_Click(object sender, RoutedEventArgs e)
    {
        var added = Library.Import(App.Store.Miners);
        App.MainWindow.ShowBanner(added == 0
            ? L("Keine neuen Pools gefunden.", "No new pools found.")
            : L($"{added} Pool(s) in die Bibliothek übernommen.", $"Added {added} pool(s) to the library."), false);
    }

    private void New_Click(object sender, RoutedEventArgs e) => _ = EditAsync(new PoolConfig(), isNew: true);

    private async Task EditAsync(PoolConfig config, bool isNew)
    {
        var dialog = new PoolConfigDialog(config, isNew) { XamlRoot = XamlRoot, RequestedTheme = ActualTheme };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary || dialog.Result is not { } saved) return;
        if (isNew) Library.Add(saved);
        else Library.Update(saved);
    }

    private async Task ConfirmDeleteAsync(PoolConfig pool)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            RequestedTheme = ActualTheme,
            Title = L("Pool-Konfiguration löschen?", "Delete pool configuration?"),
            Content = L($"„{pool.Name}“ wird nur aus der Bibliothek entfernt. Die Miner behalten ihre aktuelle Konfiguration.",
                        $"“{pool.Name}” is only removed from the library. The miners keep their current configuration."),
            PrimaryButtonText = L("Löschen", "Delete"),
            CloseButtonText = L("Abbrechen", "Cancel"),
            DefaultButton = ContentDialogButton.Close,
        };
        if (await dialog.ShowAsync() == ContentDialogResult.Primary) Library.Remove(pool.Id);
    }
}
