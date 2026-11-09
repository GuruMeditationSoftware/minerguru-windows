using System.Text.Json;
using System.Text.RegularExpressions;

namespace MinerGuru.Core.Models;

/// <summary>Reads GitHub's answer about the latest release and compares versions.</summary>
public static partial class UpdateCheck
{
    [GeneratedRegex(@"^\d+(\.\d+){0,3}$")]
    private static partial Regex VersionPattern();

    /// <summary>The version of a published, final release, without a leading <c>v</c>; <c>null</c> for anything else.</summary>
    public static string? VersionFromResponse(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            if (Flag(root, "draft") || Flag(root, "prerelease")) return null;
            if (!root.TryGetProperty("tag_name", out var tagName) || tagName.ValueKind != JsonValueKind.String) return null;
            var tag = tagName.GetString()!;
            if (tag.StartsWith('v')) tag = tag[1..];
            return VersionPattern().IsMatch(tag) ? tag : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static bool IsNewer(string candidate, string current)
    {
        var a = Parts(candidate);
        var b = Parts(current);
        for (var i = 0; i < Math.Max(a.Length, b.Length); i++)
        {
            var x = i < a.Length ? a[i] : 0;
            var y = i < b.Length ? b[i] : 0;
            if (x != y) return x > y;
        }
        return false;
    }

    private static bool Flag(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;

    private static int[] Parts(string version) =>
        version.Split('.').Select(part => int.TryParse(part, out var n) ? n : 0).ToArray();
}
