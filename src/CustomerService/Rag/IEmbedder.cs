namespace CustomerService.Rag;

/// <summary>
/// Turns text into vectors. Two methods rather than one, and that is the whole point: e5
/// models are trained with asymmetric input markers -- "query: " before a search query,
/// "passage: " before an indexed document -- and they are part of the model contract.
/// Applying them to one side only is measurably worse than applying neither. Here the two
/// cases are separate methods, so a caller cannot embed a query as a passage without writing
/// something that obviously looks wrong. There is no Embed(text) to misuse.
/// </summary>
public interface IEmbedder : IDisposable
{
    int Dimensions { get; }
    Task<float[]> EmbedQueryAsync(string query, CancellationToken ct);
    Task<float[][]> EmbedPassagesAsync(IReadOnlyList<string> passages, CancellationToken ct);
}
