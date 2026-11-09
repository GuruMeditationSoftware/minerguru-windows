using System.Globalization;

namespace MinerGuru.Core;

/// <summary>
/// UI language: German or English, like the Mac and Android app. The texts live in <c>Resources/Strings.resx</c>
/// (English) and <c>Resources/Strings.de.resx</c> (German); the build turns them into the class
/// <see cref="Strings"/> with one property per key, which reads them in this language.
/// </summary>
public static class Lang
{
    private static bool _english = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName != "de";

    static Lang() => Strings.Culture = Culture;

    /// <summary>English unless the Windows display language is German.</summary>
    public static bool English
    {
        get => _english;
        set
        {
            _english = value;
            Strings.Culture = Culture;
        }
    }

    /// <summary>Number and date format of the UI language (German notation for German).</summary>
    public static CultureInfo Culture => English ? CultureInfo.GetCultureInfo("en-US") : CultureInfo.GetCultureInfo("de-DE");
}
