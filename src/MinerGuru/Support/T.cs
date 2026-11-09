using Microsoft.UI.Xaml.Markup;

namespace MinerGuru;

/// <summary>
/// Text from the string resources, straight in XAML: <c>Text="{local:T Key=Fleet}"</c>.
/// Same key as <c>Strings.Fleet</c> in code; the value comes from <c>Strings.resx</c> or <c>Strings.de.resx</c>.
/// </summary>
[MarkupExtensionReturnType(ReturnType = typeof(string))]
public sealed partial class T : MarkupExtension
{
    public string Key { get; set; } = "";

    protected override object ProvideValue() => Strings.ResourceManager.GetString(Key, Strings.Culture) ?? Key;
}
