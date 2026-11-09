using System.Globalization;

namespace MinerGuru.Core;

/// <summary>UI language: German or English, like the Mac and Android app.</summary>
public static class Lang
{
    /// <summary>English unless the Windows display language is German.</summary>
    public static bool English { get; set; } = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName != "de";

    /// <summary>Number and date format of the UI language (German notation for German).</summary>
    public static CultureInfo Culture => English ? CultureInfo.GetCultureInfo("en-US") : CultureInfo.GetCultureInfo("de-DE");

    /// <summary>The text in the current UI language. Use with <c>using static MinerGuru.Core.Lang;</c>.</summary>
    public static string L(string de, string en) => English ? en : de;
}
