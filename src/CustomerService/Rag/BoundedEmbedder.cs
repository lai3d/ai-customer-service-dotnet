namespace CustomerService.Rag;

/// <summary>
/// Limits how many callers may be inside the native embedding call at once.
///
/// The work is CPU-bound, so admitting more callers than there are cores buys nothing but
/// contention. The .NET-specific half of the story: a thread blocked inside ONNX Runtime is
/// a thread-pool thread the pool's hill-climbing has to notice and replace, and it injects
/// replacements slowly by design. Bounding here keeps the number of blocked pool threads at
/// the core count instead of letting a burst of arrivals starve everything else the pool is
/// carrying -- including the SSE writes of turns already in flight. A limit of 0 means the
/// processor count.
/// </summary>
public sealed class BoundedEmbedder(IEmbedder inner, int limit) : IEmbedder
{
    readonly SemaphoreSlim slots = new(limit <= 0 ? Environment.ProcessorCount : limit);

    public int Dimensions => inner.Dimensions;

    public async Task<float[]> EmbedQueryAsync(string query, CancellationToken ct)
    {
        // A customer who has already gone away should not wait for a slot.
        await slots.WaitAsync(ct);
        try { return await inner.EmbedQueryAsync(query, ct); }
        finally { slots.Release(); }
    }

    public async Task<float[][]> EmbedPassagesAsync(IReadOnlyList<string> passages, CancellationToken ct)
    {
        await slots.WaitAsync(ct);
        try { return await inner.EmbedPassagesAsync(passages, ct); }
        finally { slots.Release(); }
    }

    public void Dispose() => inner.Dispose();
}
