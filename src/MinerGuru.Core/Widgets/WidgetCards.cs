using System.Text.Json.Nodes;
using static MinerGuru.Core.Lang;

namespace MinerGuru.Core.Widgets;

/// <summary>
/// The Adaptive Cards of the Windows widgets, built from a <see cref="WidgetSnapshot"/>. The widget board draws them
/// in its own style (font, light and dark, corners), so the cards only pick text sizes, weights and the board's
/// semantic colors. Texts follow the current <see cref="Lang"/>; set it from the snapshot first.
/// </summary>
public static class WidgetCards
{
    public const string Fleet = "Fleet";
    public const string SoloChance = "Solo";
    /// <summary>
    /// A tap anywhere on a widget opens this link. The widget package declares it, so the board starts MinerGuru
    /// itself, closes and leaves it in front; a widget's own action could not take the foreground.
    /// </summary>
    public const string OpenUrl = "minerguru://open";

    /// <summary>Data older than this is shown with its time and dimmed.</summary>
    public static readonly TimeSpan StaleAfter = TimeSpan.FromMinutes(15);

    /// <param name="size"><c>small</c>, <c>medium</c> or <c>large</c>.</param>
    public static string Build(string definition, string size, WidgetSnapshot? snapshot, DateTime now)
    {
        var body = new JsonArray();
        if (snapshot == null || snapshot.Miners.Count == 0)
            Empty(body, L("Keine Miner", "No miners"), L("In MinerGuru hinzufügen", "Add them in MinerGuru"));
        else if (definition == SoloChance)
            Solo(body, size, snapshot, now);
        else
            FleetCard(body, size, snapshot, now);
        var card = new JsonObject
        {
            ["type"] = "AdaptiveCard",
            ["$schema"] = "http://adaptivecards.io/schemas/adaptive-card.json",
            ["version"] = "1.5",
            ["body"] = body,
            ["selectAction"] = new JsonObject { ["type"] = "Action.OpenUrl", ["url"] = OpenUrl },
        };
        // The board centers a card in its box: filling the box keeps the figures at the top and lets a spacer push the
        // facts to the bottom. Heights as measured on the Windows 11 board, a little short of the box.
        if (MinHeight(size) is { } height) card["minHeight"] = height;
        return card.ToJsonString();
    }

    private static string? MinHeight(string size) => size switch
    {
        "medium" => "258px",
        "large" => "424px",
        _ => null,
    };

    // Fleet

    private static void FleetCard(JsonArray body, string size, WidgetSnapshot s, DateTime now)
    {
        var stale = IsStale(s, now);
        var (value, unit) = MinerFormatting.SplitHashrate(s.TotalHashRateGhs);
        var figure = Row(
            Column(Text(value, size: "ExtraLarge", weight: "Bolder", subtle: stale)),
            Column(Text(unit, subtle: true), bottom: true));
        if (size == "small")
        {
            body.Add(figure);
            body.Add(Status(s, now));
            if (!stale && s.TotalPowerW > 0) body.Add(Text(Facts(s), size: "Small", subtle: true, spacing: "None"));
            return;
        }
        body.Add(Row(Column(figure, stretch: true), Column(Status(s, now), bottom: true)));
        var facts = Row(
            Fact(L("Leistung", "Power"), s.TotalPowerW > 0 ? DisplayFormat.Integer((long)Math.Round(s.TotalPowerW)) + " W" : "–"),
            Fact(L("Effizienz", "Efficiency"), s.EfficiencyJPerTh is { } e ? DisplayFormat.Decimal(e, 1) + " J/TH" : "–"),
            Fact("Best Diff", s.BestDiff ?? "–"));
        if (size == "large")
        {
            body.Add(facts);
            body.Add(MinerRows(s, stale, 8, separator: true));
            return;
        }
        // Medium: the three miners that matter most between the figure and the facts.
        body.Add(MinerRows(s, stale, 3, separator: false));
        body.Add(Spacer());
        body.Add(facts);
    }

    /// <summary>Hot and offline miners first, then the fastest.</summary>
    private static JsonObject MinerRows(WidgetSnapshot s, bool stale, int max, bool separator)
    {
        var rows = new JsonArray();
        var ordered = s.Miners
            .OrderBy(m => LookOf(s, m) switch { Look.Hot => 0, Look.Offline => 1, Look.Checking => 3, _ => 2 })
            .ThenByDescending(m => m.HashRateGhs);
        foreach (var m in ordered.Take(max))
        {
            var look = stale ? Look.Checking : LookOf(s, m);
            rows.Add(Row(
                Column(Text("●", color: Color(look), size: "Small")),
                Column(Text(m.Name), stretch: true),
                Column(Text(m.State == WidgetSnapshot.MinerState.Offline ? "offline" : MinerFormatting.FormatHashrate(m.HashRateGhs), weight: "Bolder")),
                Column(Text(m.State == WidgetSnapshot.MinerState.Online && m.TempC is { } t ? DisplayFormat.Integer((long)Math.Round(t)) + " °C" : "", subtle: true), width: "48px", right: true)));
        }
        return new JsonObject { ["type"] = "Container", ["spacing"] = "Medium", ["separator"] = separator, ["items"] = rows };
    }

    private static string Facts(WidgetSnapshot s) =>
        DisplayFormat.Integer((long)Math.Round(s.TotalPowerW)) + " W" + (s.EfficiencyJPerTh is { } e ? " · " + DisplayFormat.Decimal(e, 1) + " J/TH" : "");

    /// <summary>One line with what matters most: a hot miner, else an offline one, else how many are online.</summary>
    private static JsonObject Status(WidgetSnapshot s, DateTime now)
    {
        if (IsStale(s, now)) return Text(AsOf(s), size: "Small", subtle: true);
        var hot = s.Hot.ToList();
        var offline = s.Offline.ToList();
        var (look, text) =
            hot.Count == 1 ? (Look.Hot, L($"{hot[0].Name} heiß", $"{hot[0].Name} hot")) :
            hot.Count > 1 ? (Look.Hot, L($"{hot.Count} heiß", $"{hot.Count} hot")) :
            offline.Count == 1 ? (Look.Offline, $"{offline[0].Name} offline") :
            offline.Count > 1 ? (Look.Offline, $"{offline.Count} offline") :
            s.Online.Any() ? (Look.Online, $"{s.Online.Count()}/{s.Miners.Count} online") :
            (Look.Checking, L("Prüfe …", "Checking …"));
        return Row(
            Column(Text("●", color: Color(look), size: "Small")),
            Column(Text(text, size: "Small", subtle: look == Look.Online || look == Look.Checking), stretch: true));
    }

    // Solo chance

    private static void Solo(JsonArray body, string size, WidgetSnapshot s, DateTime now)
    {
        var stale = IsStale(s, now);
        if (s.SoloOdds.Count == 0)
        {
            Empty(body, L("Keine Solo-Chance", "No solo chance"), L("Kein Miner online", "No miner online"));
            return;
        }
        // The widget's own header already says "Solo chance".
        if (s.SoloOdds.Count == 1)
        {
            var one = s.SoloOdds[0];
            body.Add(Row(
                Column(Text("●", color: CoinColor(one.Coin), size: "Small")),
                Column(Text(one.Coin + " · " + (stale ? AsOf(s) : one.Period), size: "Small", subtle: true), stretch: true)));
            body.Add(Text(one.Odds, size: "ExtraLarge", weight: "Bolder", subtle: stale, spacing: "Small"));
            if (one.BlockEvery is { Length: > 1 })
            {
                if (size != "small") body.Add(Spacer());
                body.Add(Text(L("Ø 1 Block alle ", "Avg. 1 block every ") + one.BlockEvery, size: "Small", subtle: true, spacing: "Small"));
            }
            return;
        }
        body.Add(Text(stale ? AsOf(s) : s.SoloOdds[0].Period, size: "Small", subtle: true));
        foreach (var coin in s.SoloOdds.Take(size == "small" ? 3 : 5))
        {
            var columns = new List<JsonObject>
            {
                Column(Text("●", color: CoinColor(coin.Coin), size: "Small")),
                Column(Text(coin.Coin, subtle: true), width: "40px"),
                Column(Text(coin.Odds, size: size == "small" ? "Default" : "Large", weight: "Bolder", subtle: stale), stretch: true),
            };
            // Medium has room for how long a block takes on average.
            if (size != "small" && coin.BlockEvery is { Length: > 1 })
                columns.Add(Column(Text(coin.BlockEvery, size: "Small", subtle: true), right: true));
            body.Add(Row([.. columns]));
        }
    }

    /// <summary>The board has no free colors: Bitcoin takes its orange-like warning tone, Bitcoin Cash green.</summary>
    private static string CoinColor(string coin) => coin switch
    {
        "BTC" => "Warning",
        "BCH" => "Good",
        _ => "Accent",
    };

    // Pieces

    private enum Look { Online, Hot, Offline, Checking }

    private static Look LookOf(WidgetSnapshot s, WidgetSnapshot.Miner m) => m.State switch
    {
        WidgetSnapshot.MinerState.Offline => Look.Offline,
        WidgetSnapshot.MinerState.Checking => Look.Checking,
        _ => s.IsHot(m) ? Look.Hot : Look.Online,
    };

    private static string Color(Look look) => look switch
    {
        Look.Online => "Good",
        Look.Hot => "Warning",
        Look.Offline => "Attention",
        _ => "Default",
    };

    public static bool IsStale(WidgetSnapshot s, DateTime now) => now - s.Time > StaleAfter;

    private static string AsOf(WidgetSnapshot s) => L("Stand ", "As of ") + s.Time.ToString("t", Culture);

    private static void Empty(JsonArray body, string title, string hint)
    {
        body.Add(Text(title, weight: "Bolder"));
        body.Add(Text(hint, size: "Small", subtle: true, wrap: true, spacing: "None"));
    }

    /// <summary>Takes the free height, so what follows sits at the bottom of the widget.</summary>
    private static JsonObject Spacer() => new() { ["type"] = "Container", ["height"] = "stretch", ["items"] = new JsonArray() };

    private static JsonObject Fact(string label, string value) => new()
    {
        ["type"] = "Column",
        ["width"] = "stretch",
        ["style"] = "emphasis",
        ["items"] = new JsonArray(Text(label, size: "Small", subtle: true), Text(value, weight: "Bolder", spacing: "None")),
    };

    private static JsonObject Row(params JsonObject[] columns) => new()
    {
        ["type"] = "ColumnSet",
        ["spacing"] = "Small",
        ["columns"] = new JsonArray(columns.Select(c => (JsonNode)c).ToArray()),
    };

    private static JsonObject Column(JsonObject item, bool stretch = false, string? width = null, bool bottom = false, bool right = false)
    {
        var column = new JsonObject
        {
            ["type"] = "Column",
            ["width"] = width ?? (stretch ? "stretch" : "auto"),
            ["verticalContentAlignment"] = bottom ? "Bottom" : "Center",
            ["items"] = new JsonArray(item),
        };
        if (right) column["horizontalAlignment"] = "Right";
        return column;
    }

    private static JsonObject Text(string text, string? size = null, string? weight = null, string? color = null,
        bool subtle = false, bool wrap = false, string? spacing = null, bool right = false)
    {
        var t = new JsonObject { ["type"] = "TextBlock", ["text"] = text, ["wrap"] = wrap, ["maxLines"] = wrap ? 2 : 1 };
        if (size != null) t["size"] = size;
        if (weight != null) t["weight"] = weight;
        if (color != null) t["color"] = color;
        if (subtle) t["isSubtle"] = true;
        if (spacing != null) t["spacing"] = spacing;
        if (right) t["horizontalAlignment"] = "Right";
        return t;
    }
}
