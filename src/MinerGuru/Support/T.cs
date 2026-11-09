using Microsoft.UI.Xaml.Markup;
using MinerGuru.Core;

namespace MinerGuru;

/// <summary>
/// Text in the UI language, straight in XAML: <c>Text="{local:T De=Flotte, En=Fleet}"</c>.
/// German and English side by side, like <c>L(de, en)</c> in code.
/// </summary>
[MarkupExtensionReturnType(ReturnType = typeof(string))]
public sealed partial class T : MarkupExtension
{
    public string De { get; set; } = "";
    public string En { get; set; } = "";

    protected override object ProvideValue() => Lang.L(De, En);
}
