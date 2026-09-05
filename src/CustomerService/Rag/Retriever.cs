using System.Diagnostics;
using CustomerService.Obs;
using Microsoft.Extensions.Logging;

namespace CustomerService.Rag;

/// <summary>
/// The whole retrieval path: embed the question, search, return passages. A plain class
/// with one method rather than a chain of composable advisors. Spring AI's
/// QuestionAnswerAdvisor also rewrote the user's message to carry the passages, which is
/// what made advisor ordering a correctness constraint; here retrieval returns passages and
/// the caller decides what to do with them, so the ordering hazard is gone by construction.
/// </summary>
public sealed class Retriever(IEmbedder embedder, VectorStore store, int topK, double threshold, ILogger<Retriever>? logger = null)
{
    readonly SearchOptions opts = new(topK, threshold);
    readonly ILogger logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<Retriever>.Instance;

    public int TopK => topK;

    /// <summary>
    /// The threshold this applies is a floor for degenerate input, not a relevance filter.
    /// With e5 the relevant and off-topic score distributions overlap, so no threshold
    /// separates them; judging relevance is the model's job, and the system prompt tells it
    /// that some of what it is given will be unrelated.
    /// </summary>
    public async Task<IReadOnlyList<Passage>> RetrieveAsync(string question, CancellationToken ct)
    {
        using var span = Tracing.Source.StartActivity("retrieve");
        float[] vector;
        using (Tracing.Source.StartActivity("embed query"))
            vector = await embedder.EmbedQueryAsync(question, ct);

        IReadOnlyList<Passage> passages;
        using (var search = Tracing.Source.StartActivity("pgvector similarity search"))
        {
            passages = await store.SearchAsync(vector, opts, ct);
            Tracing.SetRetrievalAttributes(search, opts.TopK, passages.Count, opts.Threshold, embedder.Dimensions);
        }
        // The question itself is deliberately absent from this log line, as it is from the
        // spans. A support question is often the most sensitive thing in the request, and
        // logs and traces are retained and read widely.
        logger.LogDebug("retrieved {Count} passages with top_k {TopK}", passages.Count, opts.TopK);
        return passages;
    }

    /// <summary>Retrieve restricted to one language, for cross-lingual measurement.</summary>
    public async Task<IReadOnlyList<Passage>> RetrieveInAsync(string question, string language, CancellationToken ct)
    {
        var vector = await embedder.EmbedQueryAsync(question, ct);
        return await store.SearchAsync(vector, opts with { Language = language }, ct);
    }
}
