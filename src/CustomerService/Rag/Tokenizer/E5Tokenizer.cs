using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace CustomerService.Rag.Tokenizer;

/// <summary>
/// The XLM-RoBERTa tokenizer behind multilingual-e5-small, read from HuggingFace's
/// tokenizer.json: precompiled nmt_nfkc normalisation, whitespace collapsing, Metaspace
/// pre-tokenisation and a SentencePiece Unigram model, then <c>&lt;s&gt; ... &lt;/s&gt;</c>.
///
/// Written in C# rather than bound to a native library because the .NET tokenizer packages
/// available load SentencePiece protobufs, not tokenizer.json, and this model's ids are
/// offset from its protobuf's. Getting a tokenizer subtly wrong produces plausible vectors
/// and bad rankings rather than an error, so the check is not a unit test written from the
/// same understanding: <c>tests/tokenizer-fixture.json</c> holds token ids produced by the
/// Rust implementation the Go sibling links against, for the whole corpus and every
/// measured query, and the suite asserts identity.
/// </summary>
public sealed partial class E5Tokenizer
{
    // e5 is trained at 512 tokens. Anything longer is truncated rather than rejected: an
    // over-long FAQ answer should still be findable.
    public const int MaxSequenceLength = 512;
    const char Metaspace = '▁';
    const double UnkPenalty = 10.0;

    readonly PrecompiledCharsMap charsMap;
    readonly Dictionary<string, int> ids = new(StringComparer.Ordinal);
    readonly Dictionary<string, int>.AlternateLookup<ReadOnlySpan<char>> idsBySpan;
    readonly double[] scores;
    readonly int maxPieceLength;
    readonly int unkId, bosId, eosId;
    readonly double unkScore;

    [GeneratedRegex(" {2,}")]
    private static partial Regex MultipleSpaces();

    E5Tokenizer(PrecompiledCharsMap charsMap, string[] pieces, double[] scores, int unkId, int bosId, int eosId)
    {
        this.charsMap = charsMap;
        this.scores = scores;
        this.unkId = unkId; this.bosId = bosId; this.eosId = eosId;
        double min = double.PositiveInfinity;
        for (int i = 0; i < pieces.Length; i++)
        {
            ids.TryAdd(pieces[i], i);
            if (pieces[i].Length > maxPieceLength) maxPieceLength = pieces[i].Length;
            if (scores[i] < min) min = scores[i];
        }
        unkScore = min - UnkPenalty;
        idsBySpan = ids.GetAlternateLookup<ReadOnlySpan<char>>();
    }

    public static E5Tokenizer Load(string tokenizerJsonPath)
    {
        using var doc = JsonDocument.Parse(File.ReadAllBytes(tokenizerJsonPath));
        var root = doc.RootElement;

        PrecompiledCharsMap? map = null;
        var normalizer = root.GetProperty("normalizer");
        var normalizers = normalizer.GetProperty("type").GetString() == "Sequence"
            ? normalizer.GetProperty("normalizers").EnumerateArray().ToList() : [normalizer];
        foreach (var n in normalizers)
            if (n.GetProperty("type").GetString() == "Precompiled")
                map = PrecompiledCharsMap.FromBase64(n.GetProperty("precompiled_charsmap").GetString()!);
        if (map is null) throw new InvalidDataException("tokenizer.json has no Precompiled normalizer");

        var model = root.GetProperty("model");
        if (model.GetProperty("type").GetString() != "Unigram")
            throw new InvalidDataException("tokenizer.json model is not Unigram");
        var vocab = model.GetProperty("vocab");
        var pieces = new string[vocab.GetArrayLength()];
        var scores = new double[pieces.Length];
        int i = 0;
        foreach (var entry in vocab.EnumerateArray())
        {
            pieces[i] = entry[0].GetString()!;
            scores[i] = entry[1].GetDouble();
            i++;
        }
        int unk = model.GetProperty("unk_id").GetInt32();
        int bos = -1, eos = -1;
        foreach (var t in root.GetProperty("added_tokens").EnumerateArray())
        {
            var content = t.GetProperty("content").GetString();
            if (content == "<s>") bos = t.GetProperty("id").GetInt32();
            if (content == "</s>") eos = t.GetProperty("id").GetInt32();
        }
        if (bos < 0 || eos < 0) throw new InvalidDataException("tokenizer.json lacks <s> or </s>");
        return new E5Tokenizer(map, pieces, scores, unk, bos, eos);
    }

    /// <summary>Token ids for one text, special tokens included, truncated to 512.</summary>
    public int[] Encode(string text)
    {
        var normalized = MultipleSpaces().Replace(charsMap.Normalize(text), " ").Replace(' ', Metaspace);
        if (!normalized.StartsWith(Metaspace)) normalized = Metaspace + normalized;

        var out_ = new List<int>(normalized.Length / 2 + 2) { bosId };
        // Metaspace with MergedWithNext: every piece begins with the marker.
        int start = 0;
        for (int p = 1; p <= normalized.Length; p++)
        {
            if (p == normalized.Length || normalized[p] == Metaspace)
            {
                Segment(normalized.AsSpan(start, p - start), out_);
                start = p;
            }
        }
        if (out_.Count > MaxSequenceLength - 1) out_.RemoveRange(MaxSequenceLength - 1, out_.Count - (MaxSequenceLength - 1));
        out_.Add(eosId);
        return out_.ToArray();
    }

    // Viterbi over the piece: the highest-scoring segmentation into vocabulary entries, with
    // an unknown-token fallback per code point and consecutive unknowns fused into one.
    void Segment(ReadOnlySpan<char> piece, List<int> out_)
    {
        int n = piece.Length;
        if (n == 0) return;
        var bestScore = new double[n + 1];
        var bestStart = new int[n + 1];
        var bestId = new int[n + 1];
        Array.Fill(bestStart, -1);
        bestStart[0] = 0;

        int pos = 0;
        while (pos < n)
        {
            var here = bestScore[pos];
            int runeLen = char.IsHighSurrogate(piece[pos]) && pos + 1 < n && char.IsLowSurrogate(piece[pos + 1]) ? 2 : 1;
            bool single = false;
            int maxLen = Math.Min(maxPieceLength, n - pos);
            for (int len = 1; len <= maxLen; len++)
            {
                if (!idsBySpan.TryGetValue(piece.Slice(pos, len), out var id)) continue;
                int end = pos + len;
                var candidate = here + scores[id];
                if (bestStart[end] < 0 || candidate > bestScore[end])
                {
                    bestScore[end] = candidate; bestStart[end] = pos; bestId[end] = id;
                }
                if (len == runeLen) single = true;
            }
            if (!single)
            {
                int end = pos + runeLen;
                var candidate = here + unkScore;
                if (bestStart[end] < 0 || candidate > bestScore[end])
                {
                    bestScore[end] = candidate; bestStart[end] = pos; bestId[end] = unkId;
                }
            }
            pos += runeLen;
        }

        var reversed = new List<int>();
        int at = n;
        bool inUnk = false;
        while (at > 0)
        {
            int id = bestId[at];
            if (id == unkId)
            {
                if (!inUnk) { reversed.Add(unkId); inUnk = true; }
            }
            else { reversed.Add(id); inUnk = false; }
            at = bestStart[at];
        }
        for (int i = reversed.Count - 1; i >= 0; i--) out_.Add(reversed[i]);
    }
}
