using CustomerService.Rag;
using CustomerService.Tests.Support;

namespace CustomerService.Tests;

/// <summary>
/// Retrieval quality, measured against the real embedding model and a real pgvector. No API
/// key is involved: everything up to the model call is testable, and this is where a silent
/// regression -- a changed corpus, a different embedding model, a lost prefix -- would
/// otherwise show up only as vaguer answers in production. The queries are the Java and Go
/// implementations', verbatim, so the numbers can be compared side by side.
/// </summary>
[Collection("postgres-384")]
public class RetrievalTests(Postgres384 pg, RetrievalTests.SharedEmbedder shared) : IClassFixture<RetrievalTests.SharedEmbedder>
{
    /// <summary>One 470 MB model for the class, and one ingestion into the shared database.</summary>
    public sealed class SharedEmbedder : IDisposable
    {
        public OnnxEmbedder? Embedder { get; }
        public SharedEmbedder()
        {
            if (!Repo.ModelPresent) return;
            Embedder = new OnnxEmbedder(new OnnxOptions(Repo.ModelPath, Repo.TokenizerPath, 384, "query: ", "passage: "));
        }
        public void Dispose() => Embedder?.Dispose();
    }

    static readonly SemaphoreSlim IngestOnce = new(1, 1);
    static bool ingested;

    async Task<Retriever> Retriever()
    {
        Assert.SkipUnless(Repo.ModelPresent, "embedding model not present; run scripts/fetch-deps.sh");
        var store = new VectorStore(pg.Db);
        await IngestOnce.WaitAsync();
        try
        {
            if (!ingested)
            {
                await Ingest.RunAsync(Repo.CorpusPath, shared.Embedder!, store, Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance, CancellationToken.None);
                ingested = true;
            }
        }
        finally { IngestOnce.Release(); }
        return new Retriever(shared.Embedder!, store, 8, 0);
    }

    static readonly (string query, string want)[] English =
    [
        ("I want to send something back, is it too late after three weeks?", "returns-window"),
        ("how much do I pay for delivery", "shipping-cost"),
        ("my card was rejected at checkout", "payment-declined"),
        ("when can I talk to a real person", "support-hours"),
        ("my parcel showed up broken", "returns-damaged"),
        ("can I get a different size instead", "returns-exchange"),
        ("can I still change where it gets delivered", "shipping-address-change"),
        ("I forgot my password", "account-password"),
        ("do you send orders overseas", "shipping-international"),
        ("I was billed twice", "payment-double-charge"),
    ];

    static readonly (string query, string want)[] Chinese =
    [
        ("我想退货，过了三个星期还来得及吗", "returns-window"),
        ("运费多少钱", "shipping-cost"),
        ("刷卡付款失败了", "payment-declined"),
        ("怎么才能找到人工客服", "support-hours"),
        ("包裹到的时候是坏的", "returns-damaged"),
        ("下单之后还能改地址吗", "shipping-address-change"),
        ("密码忘了怎么办", "account-password"),
        ("能寄到国外吗", "shipping-international"),
        ("同一笔订单扣了两次钱", "payment-double-charge"),
        ("想换个大一号的", "returns-exchange"),
    ];

    [Fact]
    public async Task EnglishParaphraseRetrievesTheRightEntryFirst()
    {
        var r = await Retriever();
        foreach (var (query, want) in English)
        {
            var passages = await r.RetrieveAsync(query, CancellationToken.None);
            Assert.True(passages.Count > 0, $"no passages for {query}");
            Assert.True(want == passages[0].Document.EntryId, $"top hit for \"{query}\" is {passages[0].Document.EntryId} ({passages[0].Score:F4}), want {want}");
        }
    }

    [Fact]
    public async Task ChineseParaphraseRetrievesTheRightEntryFirst()
    {
        var r = await Retriever();
        foreach (var (query, want) in Chinese)
        {
            var passages = await r.RetrieveAsync(query, CancellationToken.None);
            Assert.True(want == passages[0].Document.EntryId, $"top hit for \"{query}\" is {passages[0].Document.EntryId} ({passages[0].Score:F4}), want {want}");
            Assert.Equal("zh", passages[0].Document.Language);
        }
    }

    /// <summary>
    /// The real test of a multilingual model, and it cannot be observed on the full corpus:
    /// same-language matches score high enough that all eighteen Chinese passages outrank every
    /// English one. Isolating the English half shows whether cross-lingual retrieval works at
    /// all, which is what matters for an entry nobody has translated yet.
    /// </summary>
    [Fact]
    public async Task ChineseQuestionFindsTheEnglishPassageWhenOnlyEnglishExists()
    {
        var r = await Retriever();
        foreach (var (query, want) in new[] { ("运费多少钱", "shipping-cost"), ("包裹到的时候是坏的", "returns-damaged"), ("密码忘了怎么办", "account-password"), ("能寄到国外吗", "shipping-international") })
        {
            var passages = await r.RetrieveInAsync(query, "en", CancellationToken.None);
            Assert.True(want == passages[0].Document.EntryId, $"top English hit for \"{query}\" is {passages[0].Document.EntryId} ({passages[0].Score:F4}), want {want}");
            Assert.All(passages, p => Assert.Equal("en", p.Document.Language));
        }
    }

    /// <summary>
    /// Relevant, off-topic and degenerate inputs all score in one overlapping band, so no
    /// threshold filters relevance and none works as a floor for junk. The assertion is the
    /// overlap: if a future embedding model separates the populations, this fails and says to
    /// re-measure rather than quietly passing on a claim that has stopped being true.
    /// </summary>
    [Fact]
    public async Task NoSimilarityThresholdIsUseful()
    {
        var r = await Retriever();
        double weakestRelevant = double.PositiveInfinity;
        foreach (var (query, _) in English.Concat(Chinese))
            weakestRelevant = Math.Min(weakestRelevant, (await r.RetrieveAsync(query, CancellationToken.None))[0].Score);

        string[] offTopic = ["what is the weather like tomorrow", "recommend me a good movie", "how do I bake sourdough bread", "who won the world cup", "translate hello into french",
            "你们招聘工程师吗", "今天天气怎么样", "推荐一部好电影", "怎么做红烧肉", "世界杯谁赢了"];
        double strongestOffTopic = offTopic.Max(q => r.RetrieveAsync(q, CancellationToken.None).Result[0].Score);

        string[] degenerate = ["。。。", "...", "?", "a", "asdfghjkl", "!!!", "1234567890", "。", "，，", "x", "test", "hi", "ok", "呃", "嗯"];
        double strongestDegenerate = degenerate.Max(q => r.RetrieveAsync(q, CancellationToken.None).Result[0].Score);

        Assert.True(strongestOffTopic > weakestRelevant || strongestDegenerate > weakestRelevant,
            $"the populations no longer overlap (relevant >= {weakestRelevant:F4}, off-topic <= {strongestOffTopic:F4}, degenerate <= {strongestDegenerate:F4}); re-measure the threshold");
    }

    /// <summary>
    /// The scores agree with the Java and Go implementations to three decimal places, and that
    /// agreement is the embedding pipeline's test: three independent tokenizer implementations
    /// -- Java's DJL, Rust's tokenizers, and the C# port here -- landing on the same cosine
    /// similarity for the same query against the same passage is a stronger check than any of
    /// them could run alone.
    /// </summary>
    [Theory]
    [InlineData("my parcel showed up broken", "returns-damaged", 0.8378)]
    [InlineData("你们招聘工程师吗", null, 0.8490)]
    [InlineData("。。。", null, 0.8417)]
    public async Task ScoresAgreeWithTheSiblingImplementations(string query, string? wantEntry, double wantScore)
    {
        var r = await Retriever();
        var top = (await r.RetrieveAsync(query, CancellationToken.None))[0];
        if (wantEntry is not null) Assert.Equal(wantEntry, top.Document.EntryId);
        Assert.True(Math.Abs(top.Score - wantScore) < 0.0015, $"top score for \"{query}\" is {top.Score:F4}, the siblings measured {wantScore:F4}");
    }

    [Fact]
    public async Task OnnxEmbedderIsConcurrencySafe()
    {
        Assert.SkipUnless(Repo.ModelPresent, "embedding model not present; run scripts/fetch-deps.sh");
        var e = shared.Embedder!;
        var baseline = await e.EmbedQueryAsync("my parcel showed up broken", CancellationToken.None);
        var results = await Task.WhenAll(Enumerable.Range(0, 32).Select(i => Task.Run(() =>
            e.EmbedQueryAsync(i % 2 == 0 ? "my parcel showed up broken" : "运费多少钱", CancellationToken.None))));
        Assert.Equal(384, baseline.Length);
        Assert.InRange(baseline.Sum(x => (double)x * x), 0.999, 1.001);
        for (int i = 0; i < results.Length; i += 2)
            Assert.Equal(baseline, results[i]);
    }
}

/// <summary>
/// Not an assertion: the numbers docs/retrieval.md quotes, produced by the suite so they can be
/// re-measured rather than edited. Run with output enabled to read them.
/// </summary>
[Collection("postgres-384")]
public class RetrievalMeasurements(Postgres384 pg)
{
    [Fact]
    public async Task RecordTheNumbersTheDocumentsQuote()
    {
        Assert.SkipUnless(Repo.ModelPresent, "embedding model not present; run scripts/fetch-deps.sh");
        var output = TestContext.Current.TestOutputHelper!;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        using var embedder = new OnnxEmbedder(new OnnxOptions(Repo.ModelPath, Repo.TokenizerPath, 384, "query: ", "passage: "));
        output.WriteLine($"session start: {sw.ElapsedMilliseconds} ms");

        await embedder.EmbedQueryAsync("warm up", CancellationToken.None);
        var store = new VectorStore(pg.Db);
        sw.Restart();
        await Ingest.RunAsync(Repo.CorpusPath, embedder, store, Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance, CancellationToken.None);
        output.WriteLine($"ingest (embed + write 36 docs): {sw.ElapsedMilliseconds} ms");

        var times = new List<double>();
        for (int i = 0; i < 20; i++)
        {
            sw.Restart();
            await embedder.EmbedQueryAsync("my parcel showed up broken", CancellationToken.None);
            times.Add(sw.Elapsed.TotalMilliseconds);
        }
        times.Sort();
        output.WriteLine($"one query embedded: median {times[10]:F1} ms, min {times[0]:F1} ms, max {times[^1]:F1} ms");

        var r = new Retriever(embedder, store, 8, 0);
        sw.Restart();
        for (int i = 0; i < 20; i++) await r.RetrieveAsync("my parcel showed up broken", CancellationToken.None);
        output.WriteLine($"retrieve (embed + search): {sw.Elapsed.TotalMilliseconds / 20:F1} ms average");

        output.WriteLine("relevant (query -> top hit, score):");
        foreach (var (q, _) in new[] { ("my parcel showed up broken", ""), ("I was billed twice", ""), ("运费多少钱", ""), ("想换个大一号的", "") })
        {
            var top = (await r.RetrieveAsync(q, CancellationToken.None))[0];
            output.WriteLine($"  {q} -> {top.Document.EntryId} ({top.Document.Language}) {top.Score:F4}");
        }
        output.WriteLine("off-topic and degenerate (query -> top score):");
        foreach (var q in new[] { "你们招聘工程师吗", "recommend me a good movie", "。。。", "...", "a", "asdfghjkl" })
        {
            var top = (await r.RetrieveAsync(q, CancellationToken.None))[0];
            output.WriteLine($"  {q} -> {top.Document.EntryId} {top.Score:F4}");
        }
        output.WriteLine("cross-lingual, English only:");
        foreach (var q in new[] { "运费多少钱", "包裹到的时候是坏的" })
        {
            var top = (await r.RetrieveInAsync(q, "en", CancellationToken.None))[0];
            output.WriteLine($"  {q} -> {top.Document.EntryId} {top.Score:F4}");
        }
    }
}
