using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Xunit;

namespace MinerGuru.Core.Tests;

/// <summary>The two resource files stay in step, and every key used in XAML exists.</summary>
public class LocalizationTests
{
    private static readonly string Root = FindRoot();
    private static readonly Regex Placeholder = new(@"(?<!\{)\{(\d+)[^{}]*\}");

    private static string FindRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "src", "MinerGuru.Core"))) dir = dir.Parent;
        return dir?.FullName ?? throw new DirectoryNotFoundException("Repository root not found");
    }

    private static Dictionary<string, string> Read(string file) =>
        XDocument.Load(Path.Combine(Root, "src", "MinerGuru.Core", "Resources", file)).Root!.Elements("data")
            .ToDictionary(d => (string)d.Attribute("name")!, d => (string?)d.Element("value") ?? "");

    private static HashSet<string> Indices(string value) => Placeholder.Matches(value).Select(m => m.Groups[1].Value).ToHashSet();

    [Fact]
    public void GermanHasEveryKeyWithTheSamePlaceholders()
    {
        var english = Read("Strings.resx");
        var german = Read("Strings.de.resx");
        Assert.Equal(english.Keys.Order(), german.Keys.Order());
        Assert.All(english, entry => Assert.True(Indices(entry.Value).SetEquals(Indices(german[entry.Key])), $"{entry.Key}: placeholders differ"));
    }

    [Fact]
    public void EveryXamlKeyExists()
    {
        var english = Read("Strings.resx");
        var used = Directory.EnumerateFiles(Path.Combine(Root, "src"), "*.xaml", SearchOption.AllDirectories)
            .Where(file => !file.Split(Path.DirectorySeparatorChar).Any(part => part is "bin" or "obj"))
            .SelectMany(file => Regex.Matches(File.ReadAllText(file), @"\{local:T Key=(\w+)\}").Select(m => $"{Path.GetFileName(file)}: {m.Groups[1].Value}"))
            .ToList();
        Assert.NotEmpty(used);
        Assert.All(used, entry => Assert.True(english.ContainsKey(entry[(entry.IndexOf(": ") + 2)..]), entry));
    }

    [Fact]
    public void ReadsBothLanguages()
    {
        Assert.Equal("Fleet", Strings.ResourceManager.GetString(nameof(Strings.Fleet), CultureInfo.GetCultureInfo("en-US")));
        Assert.Equal("Flotte", Strings.ResourceManager.GetString(nameof(Strings.Fleet), CultureInfo.GetCultureInfo("de-DE")));
        // Any other language gets English.
        Assert.Equal("Fleet", Strings.ResourceManager.GetString(nameof(Strings.Fleet), CultureInfo.GetCultureInfo("fr-FR")));
    }
}
