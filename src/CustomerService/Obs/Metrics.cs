using Prometheus;

namespace CustomerService.Obs;

/// <summary>
/// Tagged by model and never by conversation id. Per-conversation tags grow cardinality
/// without limit and take the metrics backend down long before the bill does. There is
/// deliberately no way to pass a conversation id into any of these. Token spend and latency
/// are the two numbers that decide whether an LLM feature survives contact with production.
/// </summary>
public sealed class Metrics
{
    public CollectorRegistry Registry { get; }

    public Counter Tokens { get; }
    public Counter CostUsd { get; }
    public Counter ModelCalls { get; }
    public Counter Turns { get; }
    public Histogram TurnSeconds { get; }
    public Counter ToolCalls { get; }
    public Counter Unpriced { get; }
    public Histogram Retrieval { get; }

    public Metrics() : this(Prometheus.Metrics.NewCustomRegistry()) { }

    public Metrics(CollectorRegistry registry)
    {
        Registry = registry;
        var f = Prometheus.Metrics.WithCustomRegistry(registry);
        Tokens = f.CreateCounter("chat_tokens_total", "Tokens billed, by model and direction.",
            new CounterConfiguration { LabelNames = ["model", "type"] });
        CostUsd = f.CreateCounter("chat_cost_usd_total", "Estimated spend in USD, by model. Stays at zero for a model with no price entry.",
            new CounterConfiguration { LabelNames = ["model"] });
        ModelCalls = f.CreateCounter("chat_model_calls_total", "Model calls. A tool-calling turn makes at least two, and each is billed.",
            new CounterConfiguration { LabelNames = ["model", "outcome"] });
        Turns = f.CreateCounter("chat_turns_total", "Customer turns, by how they ended.",
            new CounterConfiguration { LabelNames = ["outcome"] });
        TurnSeconds = f.CreateHistogram("chat_turn_duration_seconds", "Wall time of a whole customer turn, retrieval and every model call included.",
            new HistogramConfiguration { LabelNames = ["model"], Buckets = [0.25, 0.5, 1, 2, 4, 8, 16, 32, 64] });
        ToolCalls = f.CreateCounter("chat_tool_invocations_total", "Tool invocations, by outcome.",
            new CounterConfiguration { LabelNames = ["tool", "outcome"] });
        // Without this, a model with no price entry is indistinguishable from a model that
        // cost nothing: tokens keep counting and chat_cost_usd_total stays at zero. That is
        // the failure mode of keying prices on the requested model id when the provider
        // reports a dated one, and it is silent unless something counts it.
        Unpriced = f.CreateCounter("chat_unpriced_model_calls_total", "Model calls whose tokens were counted but could not be costed, by model.",
            new CounterConfiguration { LabelNames = ["model"] });
        Retrieval = f.CreateHistogram("chat_retrieval_duration_seconds", "Query embedding plus vector search.",
            new HistogramConfiguration { Buckets = [0.001, 0.005, 0.01, 0.025, 0.05, 0.1, 0.25, 1] });
    }

    /// <summary>Meters one model call. The model is the one the provider reported.</summary>
    public void RecordUsage(string model, long inputTokens, long outputTokens, double usd, bool priced)
    {
        Tokens.WithLabels(model, "input").Inc(inputTokens);
        Tokens.WithLabels(model, "output").Inc(outputTokens);
        if (priced) CostUsd.WithLabels(model).Inc(usd);
        else Unpriced.WithLabels(model).Inc();
    }
}
