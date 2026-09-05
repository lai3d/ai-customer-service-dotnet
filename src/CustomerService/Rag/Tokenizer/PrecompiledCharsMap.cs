using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace CustomerService.Rag.Tokenizer;

/// <summary>
/// SentencePiece's precompiled normalisation table (nmt_nfkc for this model), as shipped in
/// tokenizer.json. It is a Darts double-array trie over UTF-8 keys whose leaf values index
/// into a block of NUL-terminated replacement strings.
///
/// Ported rather than approximated by <c>string.Normalize(FormKC)</c>: the two agree on
/// most text and disagree on exactly the inputs that make plausible vectors rank badly --
/// control characters, ideographic space, some compatibility forms. The tokenizer fixture
/// generated from the Rust implementation is what decides whether this port is right.
/// </summary>
public sealed class PrecompiledCharsMap
{
    readonly uint[] trie;
    readonly byte[] normalized;

    public PrecompiledCharsMap(ReadOnlySpan<byte> blob)
    {
        // Layout: u32 little-endian trie size in bytes, the trie, then the replacement strings.
        var trieSize = BinaryPrimitives.ReadUInt32LittleEndian(blob);
        trie = new uint[trieSize / 4];
        for (int i = 0; i < trie.Length; i++)
            trie[i] = BinaryPrimitives.ReadUInt32LittleEndian(blob.Slice(4 + i * 4));
        normalized = blob[(int)(4 + trieSize)..].ToArray();
    }

    public static PrecompiledCharsMap FromBase64(string base64) => new(Convert.FromBase64String(base64));

    /// <summary>
    /// Normalises a string the way the Rust tokenizer's Precompiled normaliser does: per
    /// grapheme cluster when the cluster is under six bytes, otherwise per code point.
    /// </summary>
    public string Normalize(string text)
    {
        var sb = new StringBuilder(text.Length);
        var e = StringInfo.GetTextElementEnumerator(text);
        while (e.MoveNext())
        {
            var grapheme = e.GetTextElement();
            if (Encoding.UTF8.GetByteCount(grapheme) < 6 && Transform(grapheme) is { } whole)
            {
                sb.Append(whole);
                continue;
            }
            foreach (var rune in grapheme.EnumerateRunes())
            {
                var part = rune.ToString();
                sb.Append(Transform(part) ?? part);
            }
        }
        return sb.ToString();
    }

    /// <summary>The replacement for a chunk, or null when the table has none for it.</summary>
    public string? Transform(string chunk)
    {
        Span<byte> bytes = stackalloc byte[Encoding.UTF8.GetMaxByteCount(chunk.Length)];
        int n = Encoding.UTF8.GetBytes(chunk, bytes);
        int index = FirstPrefixValue(bytes[..n]);
        if (index < 0) return null;
        int end = index;
        while (end < normalized.Length && normalized[end] != 0) end++;
        return Encoding.UTF8.GetString(normalized, index, end - index);
    }

    // Darts double-array unit layout (sentencepiece's darts.h). Each unit is one uint32.
    static bool HasLeaf(uint unit) => ((unit >> 8) & 1) == 1;
    static int Value(uint unit) => (int)(unit & 0x7FFF_FFFF);
    static uint Label(uint unit) => unit & (0x8000_0000u | 0xFF);
    static int Offset(uint unit) => (int)((unit >> 10) << (int)((unit & (1u << 9)) >> 6));

    /// <summary>
    /// The value of the shortest key that is a prefix of <paramref name="key"/>, or -1. The
    /// Rust implementation takes the first result of a common-prefix search, which is the
    /// shortest match; matching that choice matters more than matching the longest.
    /// </summary>
    int FirstPrefixValue(ReadOnlySpan<byte> key)
    {
        int pos = 0;
        uint unit = trie[pos];
        pos ^= Offset(unit);
        foreach (var c in key)
        {
            if (c == 0) break;
            pos ^= c;
            unit = trie[pos];
            if (Label(unit) != c) return -1;
            pos ^= Offset(unit);
            if (HasLeaf(unit)) return Value(trie[pos]);
        }
        return -1;
    }
}
