using System.Text.Json;
using CustomerService.Llm;
using CustomerService.Rag;

namespace CustomerService.Tests.Support;

/// <summary>
/// Makes turn tests about the turn rather than about the model. Retrieval quality is
/// measured against the real embedding model elsewhere; here the only thing that matters is
/// what the passages do to memory and to the prompt.
/// </summary>
public sealed class StubEmbedder(int dims = 8) : IEmbedder
{
    public int Dimensions => dims;

    // Not the zero vector: cosine distance against all-zeros is NaN, and a NaN score fails
    // the threshold comparison silently, so every search would return nothing.
    public float[] UnitVector() { var v = new float[dims]; v[0] = 1; return v; }

    public Task<float[]> EmbedQueryAsync(string query, CancellationToken ct) => Task.FromResult(UnitVector());
    public Task<float[][]> EmbedPassagesAsync(IReadOnlyList<string> passages, CancellationToken ct) =>
        Task.FromResult(passages.Select(_ => UnitVector()).ToArray());
    public void Dispose() { }
}

/// <summary>Records what it was asked and replies with a script, one entry per model call.</summary>
public sealed class ScriptedModel : IChatModel
{
    readonly Lock gate = new();
    public List<ModelRequest> Requests { get; } = new();
    public List<ModelResult> Script { get; init; } = new();
    /// <summary>Thrown on every call, with partial usage, the way both real clients behave.</summary>
    public ModelCallException? Error { get; init; }
    /// <summary>Runs before each reply, for tests that need to interfere mid-turn.</summary>
    public Func<int, Task>? OnCall { get; init; }
    public int Calls { get; private set; }
    public string Provider => "stub";
    public string Model => "stub-model";
    public const string ReportedModel = "stub-model-2026-01-01";

    public async Task<ModelResult> StreamAsync(ModelRequest request, Func<string, ValueTask> onText, CancellationToken ct)
    {
        int call;
        lock (gate) { Requests.Add(request); call = Calls++; }
        if (OnCall is not null) await OnCall(call);
        if (Error is not null) throw Error;
        if (call >= Script.Count) throw new InvalidOperationException($"stub ran out of script at call {call}");
        var result = Script[call];
        if (result.Text.Length > 0) await onText(result.Text);
        return result.Model.Length > 0 ? result : result with { Model = ReportedModel };
    }

    public ModelRequest LastRequest => Requests.Count > 0 ? Requests[^1] : throw new InvalidOperationException("the model was never called");

    public static ModelResult Text(string text, long input = 100, long output = 20) =>
        new(text, [], "end_turn", new Usage(input, output), ReportedModel);

    public static ModelResult ToolUse(string text, string toolName, object args, long input = 100, long output = 20, string id = "call_1") =>
        new(text, [new ToolCall(id, toolName, JsonSerializer.SerializeToElement(args))], "tool_use", new Usage(input, output), ReportedModel);
}
