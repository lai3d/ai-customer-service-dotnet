using System.Text.Json;
using System.Text.Json.Serialization;

namespace CustomerService.Rag;

/// <summary>The FAQ corpus file. Byte-identical to the Java and Go implementations'; never edit it.</summary>
public sealed record Corpus(string Version, string Notice, IReadOnlyList<CorpusEntry> Entries)
{
    public const string Source = "faq";

    public static Corpus Load(string path)
    {
        Corpus? c;
        try
        {
            c = JsonSerializer.Deserialize<Corpus>(File.ReadAllBytes(path), Json);
        }
        catch (IOException ex) { throw new InvalidDataException($"read FAQ corpus {path}: {ex.Message}", ex); }
        catch (JsonException ex) { throw new InvalidDataException($"parse FAQ corpus {path}: {ex.Message}", ex); }
        if (c is null || c.Entries.Count == 0) throw new InvalidDataException($"FAQ corpus {path} contains no entries");
        return c;
    }

    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Flattens the corpus. Both the question and the answer are embedded: embedding the
    /// question alone matches incoming phrasing most closely but loses recall whenever a
    /// customer describes their situation in the answer's vocabulary. Each language becomes
    /// its own document, which is what makes bilingual retrieval work at all; the cost is
    /// that same-language matches dominate, so cross-lingual retrieval is invisible on the
    /// full corpus and has to be tested by filtering to the other language. There is
    /// deliberately no text splitter: an FAQ entry is already the unit a question should match.
    /// </summary>
    public IReadOnlyList<Document> Documents() =>
        Entries.SelectMany(e => e.Localized.Select(l => new Document(
            Id: $"{Source}:{e.Id}:{l.Language}",
            EntryId: e.Id, Language: l.Language, Category: e.Category,
            Question: l.Question, Answer: l.Answer,
            Content: $"Q: {l.Question}\nA: {l.Answer}",
            CorpusVersion: Version))).ToList();
}

public sealed record CorpusEntry(string Id, string Category, IReadOnlyList<LocalizedEntry> Localized);
public sealed record LocalizedEntry(string Language, string Question, string Answer);

/// <summary>One indexable unit: one entry in one language.</summary>
public sealed record Document(
    string Id, string EntryId, string Language, string Category,
    string Question, string Answer, string Content, string CorpusVersion);

/// <summary>One retrieved document and how well it matched: cosine similarity in [-1, 1].</summary>
public sealed record Passage(Document Document, double Score);
