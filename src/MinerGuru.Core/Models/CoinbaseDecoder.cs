using System.Text;

namespace MinerGuru.Core.Models;

/// <summary>The coinbase transaction of a Stratum job, taken apart: height, pool tag and outputs.</summary>
public sealed record DecodedCoinbase(int? Height, byte[] ScriptSig, IReadOnlyList<DecodedCoinbase.Output> Outputs)
{
    public sealed record Output(ulong ValueSats, byte[] Script)
    {
        public bool IsDataOutput => Script.Length > 0 && Script[0] == 0x6a;
    }

    public ulong TotalSats => Outputs.Aggregate(0UL, (sum, o) => sum + o.ValueSats);

    /// <summary>Readable parts of the scriptSig after the height, e.g. <c>/solo.ckpool.org/</c>.</summary>
    public IReadOnlyList<string> PoolTags
    {
        get
        {
            var start = ScriptSig.Length > 0 ? ScriptSig[0] + 1 : 0;
            if (start >= ScriptSig.Length) return [];
            var tags = new List<string>();
            var run = new List<byte>();
            void Finish()
            {
                if (run.Count >= 4) tags.Add(Encoding.ASCII.GetString(run.ToArray()));
                run.Clear();
            }
            foreach (var value in ScriptSig[start..])
            {
                if (value is >= 0x20 and <= 0x7e) run.Add(value);
                else Finish();
            }
            Finish();
            return tags.Select(t => t.Trim()).Where(t => t.Length > 0).ToList();
        }
    }
}

/// <summary>Builds the coinbase from <c>mining.notify</c> the way a miner does and parses it.</summary>
public static class CoinbaseDecoder
{
    /// <summary>
    /// <c>coinb1 + extranonce1 + extranonce2 + coinb2</c> with a zero extranonce2. Without a usable extranonce
    /// (pools that refuse the subscribe still send work), the gap is found by trying every length until the
    /// transaction parses to its last byte.
    /// </summary>
    public static DecodedCoinbase? Decode(string coinb1, string coinb2, string? extranonce1, int? extranonce2Size)
    {
        if (BytesFromHex(coinb1) is not { } head || BytesFromHex(coinb2) is not { } tail) return null;
        if (extranonce1 != null && extranonce2Size is >= 0 and <= 32 && BytesFromHex(extranonce1) is { } nonce1 &&
            Parse([.. head, .. nonce1, .. new byte[extranonce2Size.Value], .. tail]) is { } decoded)
            return decoded;
        for (var gap = 0; gap <= 32; gap++)
            if (Parse([.. head, .. new byte[gap], .. tail]) is { } found) return found;
        return null;
    }

    /// <summary>Parses a legacy (non-witness) transaction with one input; <c>null</c> unless every byte is used.</summary>
    public static DecodedCoinbase? Parse(byte[] tx)
    {
        var reader = new ByteReader(tx);
        if (!reader.Skip(4) || reader.VarInt() != 1 || !reader.Skip(36)) return null;
        if (reader.VarInt() is not { } scriptLength || scriptLength > 100) return null;
        if (reader.Read((int)scriptLength) is not { } scriptSig || !reader.Skip(4)) return null;
        if (reader.VarInt() is not { } outputCount || outputCount is < 1 or > 1000) return null;
        var outputs = new List<DecodedCoinbase.Output>();
        for (var i = 0UL; i < outputCount; i++)
        {
            if (reader.UInt64() is not { } value) return null;
            if (reader.VarInt() is not { } length || length > 10_000) return null;
            if (reader.Read((int)length) is not { } script) return null;
            outputs.Add(new DecodedCoinbase.Output(value, script));
        }
        if (!reader.Skip(4) || !reader.IsAtEnd) return null;
        return new DecodedCoinbase(Bip34Height(scriptSig), scriptSig, outputs);
    }

    private static int? Bip34Height(byte[] scriptSig)
    {
        if (scriptSig.Length == 0) return null;
        int length = scriptSig[0];
        if (length is < 1 or > 8 || scriptSig.Length <= length) return null;
        var height = 0L;
        for (var i = length; i >= 1; i--) height = height << 8 | scriptSig[i];
        return height is >= 0 and <= int.MaxValue ? (int)height : null;
    }

    public static byte[]? BytesFromHex(string hex)
    {
        try { return Convert.FromHexString(hex); }
        catch (FormatException) { return null; }
    }

    private sealed class ByteReader(byte[] bytes)
    {
        private int _offset;

        public bool IsAtEnd => _offset == bytes.Length;

        public bool Skip(int count) => Read(count) != null;

        public byte[]? Read(int count)
        {
            if (count < 0 || _offset + count > bytes.Length) return null;
            var result = bytes[_offset..(_offset + count)];
            _offset += count;
            return result;
        }

        public ulong? UInt64() => Read(8) is { } value ? LittleEndian(value) : null;

        public ulong? VarInt()
        {
            if (Read(1) is not { } first) return null;
            var width = first[0] switch { 0xfd => 2, 0xfe => 4, 0xff => 8, _ => 0 };
            if (width == 0) return first[0];
            return Read(width) is { } value ? LittleEndian(value) : null;
        }

        private static ulong LittleEndian(byte[] value)
        {
            var result = 0UL;
            for (var i = value.Length - 1; i >= 0; i--) result = result << 8 | value[i];
            return result;
        }
    }
}
