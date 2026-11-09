using System.Text.RegularExpressions;

namespace MinerGuru.Core;

/// <summary>
/// Screenshot-safe mode: IP and MAC addresses, wallet addresses and the WLAN name are replaced by scrambled
/// characters of the same kind, so the layout stays but nothing real is visible. The same input always gives the
/// same output, so nothing flickers.
/// </summary>
public static partial class Privacy
{
    /// <summary>Set by the store from the saved setting; the formatters below read it.</summary>
    public static bool IsActive { get; set; }

    public static string Ip(string value) => IsActive ? Scramble(value) : value;
    public static string Address(string value) => IsActive ? Scramble(value) : value;
    public static string Ssid(string value) => IsActive ? Scramble(value) : value;
    public static string Mac(string value) => IsActive ? Scramble(value, hex: true) : value;
    public static string StratumUser(string value) => IsActive ? ScrambleStratumUser(value) : value;
    public static string FreeText(string value) => IsActive ? ScrambleFreeText(value) : value;

    private const string Digits = "0123456789";
    private const string Lower = "abcdefghijklmnopqrstuvwxyz";
    private const string Upper = "ABCDEFGHIJKLMNOPQRSTUVWXYZ";
    private const string HexLower = "0123456789abcdef";
    private const string HexUpper = "0123456789ABCDEF";

    /// <summary>Replaces every digit and letter by a pseudo-random one of the same kind; other characters stay.</summary>
    public static string Scramble(string text, bool hex = false)
    {
        // FNV-1a: stable across launches, unlike string.GetHashCode
        var state = 0xcbf29ce484222325UL;
        foreach (var b in System.Text.Encoding.UTF8.GetBytes(text))
        {
            state ^= b;
            state = unchecked(state * 0x100000001b3UL);
        }
        char Pick(string alphabet)
        {
            state = unchecked(state * 6364136223846793005UL + 1442695040888963407UL);
            return alphabet[(int)((state >> 33) % (ulong)alphabet.Length)];
        }
        return string.Concat(text.Select(c =>
            char.IsAsciiDigit(c) ? Pick(hex ? HexLower : Digits)
            : char.IsAsciiLetterLower(c) ? Pick(hex ? HexLower : Lower)
            : char.IsAsciiLetterUpper(c) ? Pick(hex ? HexUpper : Upper)
            : c));
    }

    /// <summary>Stratum user <c>wallet.worker</c>: only the wallet part is scrambled.</summary>
    public static string ScrambleStratumUser(string user)
    {
        var dot = user.IndexOf('.');
        return dot < 0 ? Scramble(user) : Scramble(user[..dot]) + user[dot..];
    }

    /// <summary>Scrambles IP and MAC addresses and wallet-like tokens inside free text such as log lines.</summary>
    public static string ScrambleFreeText(string text)
    {
        text = MacPattern().Replace(text, m => Scramble(m.Value, hex: true));
        text = Ipv4Pattern().Replace(text, m => Scramble(m.Value));
        return TokenPattern().Replace(text, m => Scramble(m.Value));
    }

    [GeneratedRegex(@"\b(?:[0-9A-Fa-f]{2}[:-]){5}[0-9A-Fa-f]{2}\b")]
    private static partial Regex MacPattern();

    [GeneratedRegex(@"\b(?:\d{1,3}\.){3}\d{1,3}\b")]
    private static partial Regex Ipv4Pattern();

    [GeneratedRegex(@"\b[A-Za-z0-9]{25,}\b")]
    private static partial Regex TokenPattern();
}
