// The boundary between this service and whichever model provider is configured.
// Everything above it -- memory, retrieval, the tool loop, accounting -- is written
// against these types.
using System.Text.Json;

namespace CustomerService.Llm;

public enum Role { User, Assistant }

/// <summary>
/// One turn as the provider sees it. <see cref="Native"/> carries the provider's own
/// representation of an assistant turn: Claude wants the thinking blocks it produced echoed
/// back unchanged when a tool result continues the same turn, and reconstructing them from
/// Text would silently drop them. Nothing above this namespace reads it, and nothing
/// persists it: it lives for one turn.
/// </summary>
public sealed record ModelMessage(
    Role Role,
    string Text = "",
    IReadOnlyList<ToolCall>? ToolCalls = null,
    IReadOnlyList<ToolResult>? ToolResults = null,
    object? Native = null);

public sealed record ToolCall(string Id, string Name, JsonElement Arguments);

public sealed record ToolResult(string CallId, string Content, bool IsError = false);

/// <summary>
/// What the model reads when deciding whether to call a tool. The description is prompt,
/// not documentation.
/// </summary>
public sealed record ToolDefinition(string Name, string Description, JsonElement Schema);

/// <summary>
/// One model call's request. Sampling parameters are absent on purpose, for every provider:
/// Claude Opus 5 returns HTTP 400 for temperature, top_p or top_k; GPT-5 accepts only its
/// own default. There is no property here to set one by accident.
/// </summary>
public sealed record ModelRequest(
    string System,
    IReadOnlyList<ModelMessage> Messages,
    IReadOnlyList<ToolDefinition> Tools,
    int MaxTokens);

/// <summary>
/// What one model call cost. Deliberately per call, not per turn: a tool-calling turn makes
/// at least two calls and each is billed. This loop owns the call boundary, so
/// <see cref="IChatModel.StreamAsync"/> returns exactly one call's usage and the caller
/// adds them up. No heuristic reconstructs boundaries from usage frames; the sibling
/// repositories' reliability documents have the frame counts that show why none is needed.
/// </summary>
public readonly record struct Usage(long InputTokens, long OutputTokens)
{
    public static Usage operator +(Usage a, Usage b) => new(a.InputTokens + b.InputTokens, a.OutputTokens + b.OutputTokens);
    public long Total => InputTokens + OutputTokens;
}

/// <summary>The outcome of one model call.</summary>
public sealed record ModelResult(
    string Text,
    IReadOnlyList<ToolCall> ToolCalls,
    string StopReason,
    Usage Usage,
    // What the provider says it ran, which is not always what was asked for: requesting
    // "gpt-5" yields "gpt-5-2025-08-07". Metrics and prices key on this.
    string Model,
    object? Native = null)
{
    public static readonly ModelResult Empty = new("", [], "", default, "");
    public bool WantsTools => ToolCalls.Count > 0;
}

/// <summary>
/// One provider. <see cref="StreamAsync"/> makes exactly one model call, forwarding text as
/// it arrives and returning what the call produced and what it cost.
/// </summary>
public interface IChatModel
{
    string Provider { get; }
    string Model { get; }
    Task<ModelResult> StreamAsync(ModelRequest request, Func<string, ValueTask> onText, CancellationToken ct);
}

/// <summary>
/// A model call that did not complete. It still carries whatever the provider had already
/// reported: Anthropic sends the input count at message_start, before a single token of
/// the answer, so a stream that dies half-way through -- most often because the customer
/// closed the tab -- has spent real money the caller is willing to record. A client that
/// threw a bare exception here would throw that away one layer below the comment that
/// promises otherwise.
/// </summary>
public sealed class ModelCallException : Exception
{
    public ModelCallException(string message, ModelResult partial, bool retryable, int? statusCode,
        bool cancelled, Exception? inner = null) : base(message, inner)
    {
        Partial = partial;
        Retryable = retryable;
        StatusCode = statusCode;
        Cancelled = cancelled;
    }

    /// <summary>What accumulated before the failure. Its Usage is real spend.</summary>
    public ModelResult Partial { get; }
    /// <summary>
    /// Separates the failures a client should retry from the ones it should not. A customer
    /// told "try again in a moment" when the credentials are wrong is being misled, and one
    /// told "this cannot work" when the provider is briefly overloaded is turned away.
    /// </summary>
    public bool Retryable { get; }
    public int? StatusCode { get; }
    /// <summary>The caller cancelled, usually because the client went away.</summary>
    public bool Cancelled { get; }

    public static bool IsRetryableStatus(int status) => status is 429 or 500 or 502 or 503 or 504 or 529;
}

public sealed record ModelOptions(
    string ApiKey,
    string Model,
    string BaseUrl,
    int MaxTokens,
    // Interactive settings, not batch ones. The SDKs retry twice by default with their own
    // backoff; three attempts total caps the added wait at a few seconds.
    int MaxAttempts,
    TimeSpan RequestTimeout,
    // Injectable so the clients can be driven against a fake provider. A stub implementing
    // IChatModel can satisfy any contract; the real client against a scripted HTTP handler
    // cannot, which is what makes those tests worth having.
    HttpClient? HttpClient = null);

public static class ChatModels
{
    /// <summary>
    /// Builds the configured provider. The provider is configuration, not code: everything
    /// around the model is written against <see cref="IChatModel"/>.
    /// </summary>
    public static IChatModel Create(Config.ChatConfig cfg, HttpClient? httpClient = null)
    {
        var opts = new ModelOptions(cfg.ApiKey, cfg.Model, cfg.BaseUrl, cfg.MaxTokens,
            cfg.RetryMaxAttempts, cfg.RequestTimeout, httpClient);
        return cfg.Provider switch
        {
            "anthropic" => new AnthropicChatModel(opts),
            "openai" => OpenAIProtocolChatModel.OpenAI(opts),
            "xai" => OpenAIProtocolChatModel.XAI(opts),
            // Unreachable: AppConfig rejects an unknown provider by name. Here so that adding
            // a case to one switch and not the other fails loudly rather than as a null.
            _ => throw new Config.ConfigException($"provider \"{cfg.Provider}\" has no client"),
        };
    }
}
