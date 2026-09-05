using CustomerService.Rag.Tokenizer;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace CustomerService.Rag;

/// <param name="IntraOpThreads">
/// Threads ONNX Runtime may use inside one forward pass. Null leaves the runtime's default,
/// which is the core count -- and under concurrent queries every caller's pass brings its own
/// core-count of threads, so eighteen concurrent queries contend on eighteen cores with three
/// hundred threads. See docs/benchmark.md for what that measured out to.
/// </param>
public sealed record OnnxOptions(string ModelPath, string TokenizerPath, int Dimensions, string QueryPrefix, string PassagePrefix, int? IntraOpThreads = null);

/// <summary>
/// Runs the embedding model in this process, on the CPU. Anthropic has no embedding API, so
/// a RAG path either runs a model locally or takes a dependency on a second vendor.
/// In-process costs nothing per query and needs no second API key. What it costs is a
/// native runtime in the deployment and a thread blocked in native code for each call.
///
/// ONNX Runtime's Run is documented thread-safe and the tokenizer here is immutable after
/// load; <c>OnnxEmbedderIsConcurrencySafe</c> checks it under contention rather than
/// trusting the documentation.
/// </summary>
public sealed class OnnxEmbedder : IEmbedder
{
    readonly InferenceSession session;
    readonly E5Tokenizer tokenizer;
    readonly string queryPrefix, passagePrefix;

    public OnnxEmbedder(OnnxOptions opts)
    {
        if (!File.Exists(opts.ModelPath))
            throw new FileNotFoundException($"embedding model not found at {opts.ModelPath}; run scripts/fetch-deps.sh", opts.ModelPath);
        tokenizer = E5Tokenizer.Load(opts.TokenizerPath);
        using var so = new Microsoft.ML.OnnxRuntime.SessionOptions();
        so.GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL;
        if (opts.IntraOpThreads is { } intra) so.IntraOpNumThreads = intra;
        session = new InferenceSession(opts.ModelPath, so);
        Dimensions = opts.Dimensions;
        queryPrefix = opts.QueryPrefix;
        passagePrefix = opts.PassagePrefix;
    }

    public int Dimensions { get; }

    public Task<float[]> EmbedQueryAsync(string query, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(Embed([queryPrefix + query])[0]);
    }

    public Task<float[][]> EmbedPassagesAsync(IReadOnlyList<string> passages, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(Embed(passages.Select(p => passagePrefix + p).ToList()));
    }

    /// <summary>
    /// Tokenises, runs one batched forward pass, mean-pools over the unmasked positions and
    /// L2-normalises. Mean pooling is what e5 was trained with; the [CLS] vector would give
    /// plausible-looking numbers that rank badly.
    /// </summary>
    float[][] Embed(IReadOnlyList<string> texts)
    {
        if (texts.Count == 0) return [];
        var encodings = texts.Select(tokenizer.Encode).ToArray();
        int batch = encodings.Length;
        int longest = encodings.Max(e => e.Length);

        var ids = new DenseTensor<long>([batch, longest]);
        var mask = new DenseTensor<long>([batch, longest]);
        var types = new DenseTensor<long>([batch, longest]);
        for (int i = 0; i < batch; i++)
            for (int j = 0; j < encodings[i].Length; j++)
            {
                ids[i, j] = encodings[i][j];
                mask[i, j] = 1;
            }

        using var results = session.Run(
        [
            NamedOnnxValue.CreateFromTensor("input_ids", ids),
            NamedOnnxValue.CreateFromTensor("attention_mask", mask),
            NamedOnnxValue.CreateFromTensor("token_type_ids", types),
        ]);
        var hidden = results.First().AsTensor<float>();
        int dims = hidden.Dimensions[2];

        var vectors = new float[batch][];
        for (int i = 0; i < batch; i++)
        {
            var vec = new float[dims];
            int counted = encodings[i].Length;
            for (int j = 0; j < counted; j++)
                for (int d = 0; d < dims; d++)
                    vec[d] += hidden[i, j, d];
            double norm = 0;
            for (int d = 0; d < dims; d++) { vec[d] /= counted; norm += (double)vec[d] * vec[d]; }
            norm = Math.Sqrt(norm);
            if (norm > 0) for (int d = 0; d < dims; d++) vec[d] = (float)(vec[d] / norm);
            vectors[i] = vec;
        }
        return vectors;
    }

    public void Dispose() => session.Dispose();
}
