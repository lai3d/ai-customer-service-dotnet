using System.Text;
using System.Text.Json;
using Anthropic;
using Anthropic.Exceptions;
using Anthropic.Models.Messages;
using ARole = Anthropic.Models.Messages.Role;

namespace CustomerService.Llm;

/// <summary>
/// The default provider, through the official SDK. Claude has no embedding API, which is
/// why retrieval runs a local model; for chat it is the reference implementation here.
/// </summary>
public sealed class AnthropicChatModel : IChatModel
{
    readonly IAnthropicClient client;
    readonly int maxTokens;

    public AnthropicChatModel(ModelOptions opts)
    {
        IAnthropicClient c = new AnthropicClient
        {
            ApiKey = opts.ApiKey,
            MaxRetries = Math.Max(opts.MaxAttempts - 1, 0),
            Timeout = opts.RequestTimeout,
        };
        if (opts.BaseUrl is { Length: > 0 }) c = c.WithOptions(o => o with { BaseUrl = opts.BaseUrl });
        if (opts.HttpClient is not null) c = c.WithOptions(o => o with { HttpClient = opts.HttpClient });
        client = c;
        Model = opts.Model;
        maxTokens = opts.MaxTokens;
    }

    public string Provider => "anthropic";
    public string Model { get; }

    public async Task<ModelResult> StreamAsync(ModelRequest request, Func<string, ValueTask> onText, CancellationToken ct)
    {
        var parameters = new MessageCreateParams
        {
            Model = Model,
            MaxTokens = maxTokens,
            Messages = ToAnthropicMessages(request.Messages),
            System = request.System,
            Tools = request.Tools.Count > 0 ? ToAnthropicTools(request.Tools) : null,
            // No Temperature, TopP or TopK. Claude Opus 5 rejects all three with HTTP 400,
            // and no configuration seeds one here -- the property simply is not set. Spring
            // AI needed a BeanPostProcessor to strip a value its own properties class had
            // put there in a field initialiser.
        };

        // Accumulated by hand rather than through a helper, so that what is counted is what
        // arrived: input tokens at message_start, the final output count at message_delta,
        // and the id of the message the provider says it produced.
        string messageId = "", model = "", stopReason = "";
        long inTok = 0, outTok = 0;
        var blocks = new SortedDictionary<long, BlockAcc>();
        Exception? failure = null;
        bool cancelled = false;

        try
        {
            await foreach (var ev in client.Messages.CreateStreaming(parameters, cancellationToken: ct).WithCancellation(ct))
            {
                if (ev.TryPickStart(out var start))
                {
                    messageId = start.Message.ID;
                    model = EnumText(start.Message.Model);
                    inTok = start.Message.Usage.InputTokens;
                    outTok = start.Message.Usage.OutputTokens;
                }
                else if (ev.TryPickDelta(out var delta))
                {
                    outTok = delta.Usage.OutputTokens;
                    if (delta.Usage.InputTokens is { } i) inTok = i;
                    stopReason = EnumText(delta.Delta.StopReason);
                }
                else if (ev.TryPickContentBlockStart(out var cbs))
                {
                    var acc = new BlockAcc();
                    if (cbs.ContentBlock.TryPickText(out var t)) { acc.Kind = BlockKind.Text; acc.Text.Append(t.Text); }
                    else if (cbs.ContentBlock.TryPickToolUse(out var tu)) { acc.Kind = BlockKind.ToolUse; acc.ToolId = tu.ID; acc.ToolName = tu.Name; }
                    else if (cbs.ContentBlock.TryPickThinking(out var th)) { acc.Kind = BlockKind.Thinking; acc.Text.Append(th.Thinking); acc.Signature = th.Signature; }
                    else if (cbs.ContentBlock.TryPickRedactedThinking(out var rt)) { acc.Kind = BlockKind.RedactedThinking; acc.Data = rt.Data; }
                    blocks[cbs.Index] = acc;
                }
                else if (ev.TryPickContentBlockDelta(out var cbd) && blocks.TryGetValue(cbd.Index, out var acc))
                {
                    if (cbd.Delta.TryPickText(out var t))
                    {
                        // Only visible text is forwarded. Thinking blocks stream as their own
                        // delta type and are not the customer's business.
                        acc.Text.Append(t.Text);
                        if (t.Text.Length > 0) await onText(t.Text);
                    }
                    else if (cbd.Delta.TryPickInputJson(out var j)) acc.Json.Append(j.PartialJson);
                    else if (cbd.Delta.TryPickThinking(out var th)) acc.Text.Append(th.Thinking);
                    else if (cbd.Delta.TryPickSignature(out var sig)) acc.Signature = sig.Signature;
                }
            }
        }
        catch (OperationCanceledException ex)
        {
            failure = ex;
            cancelled = true;
        }
        catch (Exception ex)
        {
            failure = ex;
        }

        var result = Assemble(blocks, stopReason, new Usage(inTok, outTok), model.Length > 0 ? model : Model, complete: failure is null);

        if (failure is null) return result;

        // A failure does not return early, because by the time one happens the provider has
        // usually already told us what it billed. The partial result travels with the
        // exception so the caller can record it.
        int? status = failure is AnthropicApiException api ? (int?)api.StatusCode : null;
        bool retryable = status is { } s ? ModelCallException.IsRetryableStatus(s) : !cancelled;
        throw new ModelCallException(
            cancelled ? "model call cancelled" : $"model call failed: {failure.Message}",
            result, retryable, status, cancelled, failure);
    }

    static ModelResult Assemble(SortedDictionary<long, BlockAcc> blocks, string stopReason, Usage usage, string model, bool complete)
    {
        var text = new StringBuilder();
        var calls = new List<ToolCall>();
        List<ContentBlockParam> native = [];
        foreach (var acc in blocks.Values)
        {
            switch (acc.Kind)
            {
                case BlockKind.Text:
                    text.Append(acc.Text);
                    native.Add(new TextBlockParam { Text = acc.Text.ToString() });
                    break;
                case BlockKind.Thinking:
                    // The signature must be preserved: the API rejects a tampered block.
                    native.Add(new ThinkingBlockParam { Thinking = acc.Text.ToString(), Signature = acc.Signature ?? "" });
                    break;
                case BlockKind.RedactedThinking:
                    native.Add(new RedactedThinkingBlockParam { Data = acc.Data ?? "" });
                    break;
                case BlockKind.ToolUse:
                    var json = acc.Json.Length == 0 ? "{}" : acc.Json.ToString();
                    JsonElement args;
                    try { args = JsonDocument.Parse(json).RootElement.Clone(); }
                    catch (JsonException) { args = JsonDocument.Parse("{}").RootElement.Clone(); }
                    calls.Add(new ToolCall(acc.ToolId ?? "", acc.ToolName ?? "", args));
                    native.Add(new ToolUseBlockParam
                    {
                        ID = acc.ToolId ?? "", Name = acc.ToolName ?? "",
                        Input = args.ValueKind == JsonValueKind.Object
                            ? args.EnumerateObject().ToDictionary(p => p.Name, p => p.Value)
                            : new Dictionary<string, JsonElement>(),
                    });
                    break;
            }
        }
        // Native exists so a tool round can echo an assistant turn back unchanged, and a
        // half-streamed one is not something to send anywhere.
        object? echo = complete && native.Count > 0 ? new MessageParam { Role = ARole.Assistant, Content = native } : null;
        return new ModelResult(text.ToString(), calls, stopReason, usage, model, echo);
    }

    static List<MessageParam> ToAnthropicMessages(IReadOnlyList<ModelMessage> messages)
    {
        var out_ = new List<MessageParam>(messages.Count);
        foreach (var m in messages)
        {
            // An assistant turn that is being continued goes back exactly as it arrived.
            // Rebuilding it from Text would drop the thinking blocks Claude expects to see
            // again, and the tool_use ids would have to be invented.
            if (m.Native is MessageParam native) { out_.Add(native); continue; }
            if (m.Role == Role.Assistant)
            {
                if (m.Text.Length > 0)
                    out_.Add(new MessageParam { Role = ARole.Assistant, Content = m.Text });
                continue;
            }
            List<ContentBlockParam> blocks = [];
            foreach (var r in m.ToolResults ?? [])
                blocks.Add(new ToolResultBlockParam { ToolUseID = r.CallId, Content = r.Content, IsError = r.IsError });
            if (m.Text.Length > 0) blocks.Add(new TextBlockParam { Text = m.Text });
            if (blocks.Count > 0) out_.Add(new MessageParam { Role = ARole.User, Content = blocks });
        }
        return out_;
    }

    static List<ToolUnion> ToAnthropicTools(IReadOnlyList<ToolDefinition> tools)
    {
        var out_ = new List<ToolUnion>(tools.Count);
        foreach (var t in tools)
        {
            Dictionary<string, JsonElement> properties = t.Schema.TryGetProperty("properties", out var props) && props.ValueKind == JsonValueKind.Object
                ? props.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone()) : new();
            List<string> required = t.Schema.TryGetProperty("required", out var req) && req.ValueKind == JsonValueKind.Array
                ? req.EnumerateArray().Select(x => x.GetString() ?? "").ToList() : [];
            out_.Add(new Tool { Name = t.Name, Description = t.Description, InputSchema = new() { Properties = properties, Required = required } });
        }
        return out_;
    }

    // ApiEnum.ToString() returns the JSON encoding, quotes included. Caught by the probe
    // that preceded this repository: `stopReason != "tool_use"` was always true and a loop
    // stopped after one call. The wire value is what the metrics and the loop want.
    internal static string EnumText(object? value) => value?.ToString()?.Trim('"') ?? "";

    enum BlockKind { Other, Text, ToolUse, Thinking, RedactedThinking }

    sealed class BlockAcc
    {
        public BlockKind Kind;
        public readonly StringBuilder Text = new();
        public readonly StringBuilder Json = new();
        public string? ToolId, ToolName, Signature, Data;
    }
}
