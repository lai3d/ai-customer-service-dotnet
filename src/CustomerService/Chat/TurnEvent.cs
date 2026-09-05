using System.Text.Json.Serialization;

namespace CustomerService.Chat;

/// <summary>
/// A turn emits typed events rather than bare tokens. A chat widget reads Message and Error
/// and ignores the rest. Everything else is there because the interesting part of this
/// system is the part a widget hides: which passages retrieval found and how well they
/// scored, which tools ran and what they decided, and what the turn cost.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(RetrievalEvent), "retrieval")]
[JsonDerivedType(typeof(ToolEvent), "tool")]
[JsonDerivedType(typeof(MessageEvent), "message")]
[JsonDerivedType(typeof(UsageEvent), "usage")]
public abstract record TurnEvent
{
    /// <summary>The SSE event name, and the JSON discriminator.</summary>
    public abstract string Name { get; }
}

/// <summary>Emitted before the model is called, so it arrives while the model is still thinking and survives a failed model call.</summary>
public sealed record RetrievalEvent(IReadOnlyList<PassageSummary> Passages) : TurnEvent
{
    [JsonIgnore] public override string Name => "retrieval";
}

public sealed record PassageSummary(string EntryId, string Language, double Score, string Question);

public sealed record ToolEvent(ToolSummary Tool) : TurnEvent
{
    [JsonIgnore] public override string Name => "tool";
}

public sealed record ToolSummary(string Name, string Outcome);

public sealed record MessageEvent(string Text) : TurnEvent
{
    [JsonIgnore] public override string Name => "message";
}

public sealed record UsageEvent(UsageSummary Usage) : TurnEvent
{
    [JsonIgnore] public override string Name => "usage";
}

public sealed record UsageSummary(
    string Model,
    // Why this is not called "the model call": a tool-calling turn makes at least two, and
    // each is billed.
    int ModelCalls,
    long InputTokens,
    long OutputTokens,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] double CostUsd,
    long Millis,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] string? TraceId);
