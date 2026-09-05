using System.Text.Json;
using CustomerService.Rag.Tokenizer;
using CustomerService.Tests.Support;

namespace CustomerService.Tests;

public class TokenizerTests
{
    sealed record Case(string Text, int[] Ids);

    /// <summary>
    /// The fixture holds token ids produced by the Rust HuggingFace tokenizer -- the one the
    /// Go implementation links against -- for the whole corpus with its passage prefix, every
    /// measured query with its query prefix, and inputs chosen to exercise the normaliser:
    /// full-width letters, an ideographic space, enclosed digits, a ligature, emoji, tabs and
    /// runs of spaces. Identity here is what makes the retrieval scores comparable to the
    /// sibling implementations' at all; a tokenizer that is subtly wrong produces plausible
    /// vectors and bad rankings rather than an error.
    /// </summary>
    [Fact]
    public void TokenIdsAreIdenticalToTheRustTokenizersForEveryFixtureCase()
    {
        Assert.SkipUnless(Repo.ModelPresent, "embedding model not present; run scripts/fetch-deps.sh");
        var tokenizer = E5Tokenizer.Load(Repo.TokenizerPath);
        var cases = JsonSerializer.Deserialize<List<Case>>(
            File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "tokenizer-fixture.json")),
            new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        Assert.True(cases.Count >= 70, "the fixture should cover the corpus and the measured queries");

        var failures = new List<string>();
        foreach (var c in cases)
        {
            var got = tokenizer.Encode(c.Text);
            if (!got.SequenceEqual(c.Ids))
                failures.Add($"{c.Text[..Math.Min(40, c.Text.Length)]}\n   want {string.Join(",", c.Ids)}\n   got  {string.Join(",", got)}");
        }
        Assert.True(failures.Count == 0, $"{failures.Count} of {cases.Count} cases differ:\n" + string.Join("\n", failures));
    }

    [Fact]
    public void EveryEncodingIsBoundedBySpecialTokens()
    {
        Assert.SkipUnless(Repo.ModelPresent, "embedding model not present; run scripts/fetch-deps.sh");
        var tokenizer = E5Tokenizer.Load(Repo.TokenizerPath);
        var ids = tokenizer.Encode("query: " + string.Join(' ', Enumerable.Repeat("a long question about returns", 200)));
        Assert.Equal(E5Tokenizer.MaxSequenceLength, ids.Length);
        Assert.Equal(0, ids[0]);
        Assert.Equal(2, ids[^1]);
    }
}
