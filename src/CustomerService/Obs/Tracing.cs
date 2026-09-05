using System.Diagnostics;

namespace CustomerService.Obs;

/// <summary>
/// Tracing matters more for this kind of service than for an ordinary one. A single turn is
/// retrieval, then a model call, then possibly a tool call and a second model call. Metrics
/// can tell you a turn took eight seconds; only a trace says which of those it was.
/// Attribute names follow OpenTelemetry's GenAI semantic conventions, so nothing here
/// invents a vocabulary.
/// </summary>
public static class Tracing
{
    public const string ServiceName = "ai-customer-service-dotnet";
    public static readonly ActivitySource Source = new(ServiceName);

    public const string AttrGenAISystem = "gen_ai.system";
    public const string AttrGenAIRequestModel = "gen_ai.request.model";
    public const string AttrGenAIResponseModel = "gen_ai.response.model";
    public const string AttrGenAIInputTokens = "gen_ai.usage.input_tokens";
    public const string AttrGenAIOutputTokens = "gen_ai.usage.output_tokens";
    public const string AttrGenAIFinishReason = "gen_ai.response.finish_reasons";

    /// <summary>The current trace id, for handing a response a way to be found in the backend. Empty when nothing is traced.</summary>
    public static string TraceId(Activity? activity) =>
        activity is { } a && a.TraceId != default ? a.TraceId.ToString() : "";

    /// <summary>
    /// Describes a vector search without describing the search. The customer's question is
    /// deliberately absent, and that omission is the point: Spring AI attached the query
    /// text to every vector-store span unconditionally, found by reading a customer's
    /// question back out of Jaeger. Everything that makes the span useful is kept.
    /// </summary>
    public static void SetRetrievalAttributes(Activity? span, int topK, int returned, double threshold, int dimensions)
    {
        if (span is null) return;
        span.SetTag("db.vector.query.top_k", topK);
        span.SetTag("db.vector.query.returned", returned);
        span.SetTag("db.vector.query.similarity_threshold", threshold);
        span.SetTag("db.vector.query.dimensions", dimensions);
        span.SetTag("db.system", "postgresql");
        span.SetTag("db.operation.name", "similarity_search");
    }
}
