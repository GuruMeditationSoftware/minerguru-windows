using System.Security.Cryptography;
using System.Text;

namespace MinerGuru.Core.Models;

/// <summary>
/// Converts between addresses and output scripts for the SHA-256 coins MinerGuru knows:
/// Base58 (P2PKH, P2SH), Bech32/Bech32m (SegWit, Taproot) and CashAddr (BCH, eCash).
/// </summary>
public static class CoinAddress
{
    private sealed record Format(byte P2pkh, byte P2sh, string? Bech32Hrp, string? CashAddrPrefix);

    private static Format FormatFor(MinedCoin coin) => coin switch
    {
        MinedCoin.Bch => new(0x00, 0x05, null, "bitcoincash"),
        MinedCoin.Xec => new(0x00, 0x05, null, "ecash"),
        MinedCoin.Bsv => new(0x00, 0x05, null, null),
        MinedCoin.Dgb => new(0x1e, 0x3f, "dgb", null),
        MinedCoin.Nmc => new(0x34, 0x0d, "nc", null),
        MinedCoin.Ppc => new(0x37, 0x75, "pc", null),
        _ => new(0x00, 0x05, "bc", null),
    };

    // Address → script

    /// <summary>
    /// Output script that pays <paramref name="address"/>, independent of the coin: a CashAddr and the legacy
    /// address of the same key give the same script. <c>null</c> for account names and anything unreadable.
    /// </summary>
    public static byte[]? ScriptPubKey(string address)
    {
        var value = address.Trim();
        // Some pools want a mode in front (`solo:<address>` at Kryptex).
        var colon = value.LastIndexOf(':');
        var lower = value.ToLowerInvariant();
        if (colon >= 0 && !lower.StartsWith("bitcoincash:", StringComparison.Ordinal) && !lower.StartsWith("ecash:", StringComparison.Ordinal))
            value = value[(colon + 1)..];
        return CashAddrScript(value) ?? SegwitScript(value) ?? Base58Script(value);
    }

    private static readonly HashSet<byte> P2pkhVersions = [0x00, 0x1e, 0x34, 0x37, 0x6f];
    private static readonly HashSet<byte> P2shVersions = [0x05, 0x3f, 0x0d, 0x75, 0xc4];

    private static byte[] P2pkhScript(byte[] hash) => [0x76, 0xa9, 0x14, .. hash, 0x88, 0xac];
    private static byte[] P2shScript(byte[] hash) => [0xa9, 0x14, .. hash, 0x87];

    private static byte[]? Base58Script(string address)
    {
        if (Base58CheckDecode(address) is not { Length: 21 } payload) return null;
        var hash = payload[1..];
        if (P2pkhVersions.Contains(payload[0])) return P2pkhScript(hash);
        if (P2shVersions.Contains(payload[0])) return P2shScript(hash);
        return null;
    }

    private static byte[]? SegwitScript(string address)
    {
        if (SegwitDecode(address) is not { } decoded) return null;
        var opcode = decoded.Version == 0 ? (byte)0 : (byte)(0x50 + decoded.Version);
        return [opcode, (byte)decoded.Program.Length, .. decoded.Program];
    }

    private static byte[]? CashAddrScript(string address)
    {
        var lower = address.ToLowerInvariant();
        var colon = lower.IndexOf(':');
        // Pools accept CashAddr without prefix; the checksum tells which coin it belongs to.
        (string Prefix, string Payload)[] candidates = colon >= 0
            ? [(lower[..colon], lower[(colon + 1)..])]
            : [("bitcoincash", lower), ("ecash", lower), ("bchtest", lower), ("ectest", lower)];
        foreach (var (prefix, payload) in candidates)
        {
            if (CashAddrDecode(prefix, payload) is not { } decoded) continue;
            return decoded.Type switch
            {
                0 when decoded.Hash.Length == 20 => P2pkhScript(decoded.Hash),
                1 when decoded.Hash.Length == 20 => P2shScript(decoded.Hash),
                _ => null,
            };
        }
        return null;
    }

    // Script → address

    /// <summary>Address an output script pays, in the usual notation of <paramref name="coin"/>; <c>null</c> for scripts without one.</summary>
    public static string? Address(byte[] script, MinedCoin coin)
    {
        var format = FormatFor(coin);
        if (script.Length == 25 && script[0] == 0x76 && script[1] == 0xa9 && script[2] == 0x14 && script[23] == 0x88 && script[24] == 0xac)
        {
            var hash = script[3..23];
            return format.CashAddrPrefix is { } prefix ? CashAddrEncode(prefix, 0, hash) : Base58CheckEncode([format.P2pkh, .. hash]);
        }
        if (script.Length == 23 && script[0] == 0xa9 && script[1] == 0x14 && script[22] == 0x87)
        {
            var hash = script[2..22];
            return format.CashAddrPrefix is { } prefix ? CashAddrEncode(prefix, 1, hash) : Base58CheckEncode([format.P2sh, .. hash]);
        }
        if (format.Bech32Hrp is { } hrp && script.Length >= 4 && script[1] == script.Length - 2 && (script[0] == 0 || script[0] is >= 0x51 and <= 0x60))
            return SegwitEncode(hrp, script[0] == 0 ? (byte)0 : (byte)(script[0] - 0x50), script[2..]);
        return null;
    }

    // Base58Check

    private const string Base58Alphabet = "123456789ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz";

    private static byte[] DoubleSha256(byte[] bytes) => SHA256.HashData(SHA256.HashData(bytes));

    public static string Base58CheckEncode(byte[] payload)
    {
        byte[] bytes = [.. payload, .. DoubleSha256(payload)[..4]];
        var digits = new List<int>();
        foreach (var value in bytes)
        {
            int carry = value;
            for (var i = 0; i < digits.Count; i++)
            {
                carry += digits[i] << 8;
                digits[i] = carry % 58;
                carry /= 58;
            }
            while (carry > 0)
            {
                digits.Add(carry % 58);
                carry /= 58;
            }
        }
        var zeros = bytes.TakeWhile(b => b == 0).Count();
        var text = new StringBuilder(new string('1', zeros));
        for (var i = digits.Count - 1; i >= 0; i--) text.Append(Base58Alphabet[digits[i]]);
        return text.ToString();
    }

    public static byte[]? Base58CheckDecode(string text)
    {
        if (text.Length is 0 or > 64) return null;
        var bytes = new List<int>();
        foreach (var c in text)
        {
            var carry = Base58Alphabet.IndexOf(c);
            if (carry < 0) return null;
            for (var i = 0; i < bytes.Count; i++)
            {
                carry += bytes[i] * 58;
                bytes[i] = carry & 0xff;
                carry >>= 8;
            }
            while (carry > 0)
            {
                bytes.Add(carry & 0xff);
                carry >>= 8;
            }
        }
        var zeros = text.TakeWhile(c => c == '1').Count();
        byte[] decoded = [.. new byte[zeros], .. Enumerable.Reverse(bytes).Select(b => (byte)b)];
        if (decoded.Length <= 4) return null;
        var payload = decoded[..^4];
        return decoded[^4..].SequenceEqual(DoubleSha256(payload)[..4]) ? payload : null;
    }

    // Bech32 / Bech32m (BIP 173, BIP 350)

    private const string Bech32Charset = "qpzry9x8gf2tvdw0s3jn54khce6mua7l";
    private const uint Bech32Constant = 1;
    private const uint Bech32mConstant = 0x2bc830a3;

    private static uint Bech32Polymod(IEnumerable<byte> values)
    {
        uint[] generator = [0x3b6a57b2, 0x26508e6d, 0x1ea119fa, 0x3d4233dd, 0x2a1462b3];
        uint chk = 1;
        foreach (var value in values)
        {
            var top = chk >> 25;
            chk = ((chk & 0x1ffffff) << 5) ^ value;
            for (var i = 0; i < 5; i++)
                if (((top >> i) & 1) == 1) chk ^= generator[i];
        }
        return chk;
    }

    private static byte[] HrpExpand(string hrp) =>
        [.. hrp.Select(c => (byte)(c >> 5)), 0, .. hrp.Select(c => (byte)(c & 31))];

    private static string? SegwitEncode(string hrp, byte version, byte[] program)
    {
        if (ConvertBits(program, 8, 5, true) is not { } converted) return null;
        byte[] data = [version, .. converted];
        var constant = version == 0 ? Bech32Constant : Bech32mConstant;
        var polymod = Bech32Polymod([.. HrpExpand(hrp), .. data, 0, 0, 0, 0, 0, 0]) ^ constant;
        var checksum = Enumerable.Range(0, 6).Select(i => (byte)((polymod >> (5 * (5 - i))) & 31));
        return hrp + "1" + new string(data.Concat(checksum).Select(d => Bech32Charset[d]).ToArray());
    }

    private static (string Hrp, byte Version, byte[] Program)? SegwitDecode(string address)
    {
        var lower = address.ToLowerInvariant();
        if ((lower != address && address.ToUpperInvariant() != address) || lower.Length > 90) return null;
        var separator = lower.LastIndexOf('1');
        if (separator <= 0) return null;
        var hrp = lower[..separator];
        var rest = lower[(separator + 1)..];
        if (rest.Length < 7) return null;
        var data = new byte[rest.Length];
        for (var i = 0; i < rest.Length; i++)
        {
            var value = Bech32Charset.IndexOf(rest[i]);
            if (value < 0) return null;
            data[i] = (byte)value;
        }
        var version = data[0];
        var polymod = Bech32Polymod([.. HrpExpand(hrp), .. data]);
        if (version > 16 || polymod != (version == 0 ? Bech32Constant : Bech32mConstant)) return null;
        if (ConvertBits(data[1..^6], 5, 8, false) is not { Length: >= 2 and <= 40 } program) return null;
        if (version == 0 && program.Length is not (20 or 32)) return null;
        return (hrp, version, program);
    }

    private static byte[]? ConvertBits(byte[] data, int from, int to, bool pad)
    {
        var acc = 0;
        var bits = 0;
        var result = new List<byte>();
        var maxValue = (1 << to) - 1;
        foreach (var value in data)
        {
            if (value >> from != 0) return null;
            acc = ((acc << from) | value) & 0xffffff;
            bits += from;
            while (bits >= to)
            {
                bits -= to;
                result.Add((byte)((acc >> bits) & maxValue));
            }
        }
        if (pad)
        {
            if (bits > 0) result.Add((byte)((acc << (to - bits)) & maxValue));
        }
        else if (bits >= from || ((acc << (to - bits)) & maxValue) != 0)
        {
            return null;
        }
        return [.. result];
    }

    // CashAddr

    private static ulong CashAddrPolymod(IEnumerable<byte> values)
    {
        ulong[] generator = [0x98f2bc8e61, 0x79b76d99e2, 0xf33e5fb3c4, 0xae2eabe2a8, 0x1e4f43e470];
        ulong c = 1;
        foreach (var value in values)
        {
            var top = (byte)(c >> 35);
            c = ((c & 0x07ffffffff) << 5) ^ value;
            for (var i = 0; i < 5; i++)
                if (((top >> i) & 1) == 1) c ^= generator[i];
        }
        return c ^ 1;
    }

    private static byte[] CashAddrPrefixBits(string prefix) => [.. prefix.Select(c => (byte)(c & 31)), 0];

    private static string? CashAddrEncode(string prefix, byte type, byte[] hash)
    {
        // Version byte: type in bits 3–6, size code 0 for 160-bit hashes.
        if (ConvertBits([(byte)(type << 3), .. hash], 8, 5, true) is not { } payload) return null;
        var polymod = CashAddrPolymod([.. CashAddrPrefixBits(prefix), .. payload, .. new byte[8]]);
        var checksum = Enumerable.Range(0, 8).Select(i => (byte)((polymod >> (5 * (7 - i))) & 31));
        return prefix + ":" + new string(payload.Concat(checksum).Select(d => Bech32Charset[d]).ToArray());
    }

    private static (byte Type, byte[] Hash)? CashAddrDecode(string prefix, string payload)
    {
        if (payload.Length <= 8) return null;
        var data = new byte[payload.Length];
        for (var i = 0; i < payload.Length; i++)
        {
            var value = Bech32Charset.IndexOf(payload[i]);
            if (value < 0) return null;
            data[i] = (byte)value;
        }
        if (CashAddrPolymod([.. CashAddrPrefixBits(prefix), .. data]) != 0) return null;
        if (ConvertBits(data[..^8], 5, 8, false) is not { Length: > 0 } bytes) return null;
        return ((byte)(bytes[0] >> 3), bytes[1..]);
    }
}
