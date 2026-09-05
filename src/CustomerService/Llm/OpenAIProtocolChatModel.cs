using System.ClientModel;
using System.ClientModel.Primitives;
using System.Text;
using System.Text.Json;
using OpenAI;
using OpenAI.Chat;

namespace CustomerService.Llm;

/// <summary>
/// Speaks OpenAI's chat completions wire protocol.
///
/// Two providers use it, and they are two providers rather than one. xAI speaks this
/// protocol, so reimplementing streaming, tool calling and retry for Grok would be pure
/// cost -- but selecting "openai", putting an xAI key in OPENAI_API_KEY and overriding the
/// base URL works and lies: the configuration then says OpenAI everywhere while talking to
/// xAI. The provider name, credentials, base URL and model are xAI's own; only the client is
/// shared. What this does not paper over: xAI's compatibility is xAI's to maintain.
/// </summary>
public sealed class OpenAIProtocolChatModel : IChatModel
{
    readonly ChatClient chat;
    readonly int maxTokens;

    OpenAIProtocolChatModel(string provider, ModelOptions opts)
    {
        var options = new OpenAIClientOptions
        {
            NetworkTimeout = opts.RequestTimeout,
            RetryPolicy = new ClientRetryPolicy(Math.Max(opts.MaxAttempts - 1, 0)),
        };
        if (opts.BaseUrl is { Length: > 0 }) options.Endpoint = new Uri(opts.BaseUrl);
        if (opts.HttpClient is not null) options.Transport = new HttpClientPipelineTransport(opts.HttpClient);
        chat = new OpenAIClient(new ApiKeyCredential(opts.ApiKey), options).GetChatClient(opts.Model);
        Provider = provider;
        Model = opts.Model;
        maxTokens = opts.MaxTokens;
    }

    public static OpenAIProtocolChatModel OpenAI(ModelOptions opts) => new("openai", opts);
    public static OpenAIProtocolChatModel XAI(ModelOptions opts) => new("xai", opts);

    public string Provider { get; }
    public string Model { get; }

    public async Task<ModelResult> StreamAsync(ModelRequest request, Func<string, ValueTask> onText, CancellationToken ct)
    {
        var options = new ChatCompletionOptions
        {
            MaxOutputTokenCount = maxTokens,
            // No Temperature, TopP or penalties. GPT-5 rejects a temperature other than its
            // own default outright.
        };
        foreach (var t in request.Tools)
            options.Tools.Add(ChatTool.CreateFunctionTool(t.Name, t.Description, BinaryData.FromString(t.Schema.GetRawText())));

        var text = new StringBuilder();
        var calls = new SortedDictionary<int, (string id, string name, StringBuilder args)>();
        string model = "", finish = "";
        long inTok = 0, outTok = 0;
        Exception? failure = null;
        bool cancelled = false;

        try
        {
            // The SDK asks for stream_options.include_usage itself; the test against a fake
            // provider asserts it stays on the wire, because without it the response carries
            // no usage at all and the failure is silent -- the budget never fires and the
            // cost meters read zero while real money is spent.
            await foreach (var update in chat.CompleteChatStreamingAsync(ToOpenAIMessages(request), options, ct).WithCancellation(ct))
            {
                if (update.Model is { Length: > 0 } m) model = m;
                foreach (var part in update.ContentUpdate)
                {
                    if (part.Text is { Length: > 0 } piece)
                    {
                        text.Append(piece);
                        await onText(piece);
                    }
                }
                foreach (var tc in update.ToolCallUpdates)
                {
                    if (!calls.TryGetValue(tc.Index, out var acc))
                    {
                        acc = ("", "", new StringBuilder());
                        calls[tc.Index] = acc;
                    }
                    if (tc.ToolCallId is { Length: > 0 } id) acc.id = id;
                    if (tc.FunctionName is { Length: > 0 } name) acc.name = name;
                    if (tc.FunctionArgumentsUpdate is { } args) acc.args.Append(args.ToString());
                    calls[tc.Index] = acc;
                }
                if (update.FinishReason is { } fr) finish = fr.ToString().ToLowerInvariant();
                if (update.Usage is { } u)
                {
                    // One call in, one call's usage out: the protocol sends usage on a single
                    // final chunk. A call that dies mid-stream genuinely has nothing to
                    // report, unlike the same abort on Anthropic.
                    inTok = u.InputTokenCount;
                    outTok = u.OutputTokenCount;
                }
            }
        }
        catch (OperationCanceledException ex) { failure = ex; cancelled = true; }
        catch (Exception ex) { failure = ex; }

        var toolCalls = new List<ToolCall>();
        foreach (var (_, acc) in calls)
        {
            JsonElement args;
            try { args = JsonDocument.Parse(acc.args.Length == 0 ? "{}" : acc.args.ToString()).RootElement.Clone(); }
            catch (JsonException) { args = JsonDocument.Parse("{}").RootElement.Clone(); }
            toolCalls.Add(new ToolCall(acc.id, acc.name, args));
        }
        var result = new ModelResult(text.ToString(), toolCalls, MapFinish(finish), new Usage(inTok, outTok),
            model.Length > 0 ? model : Model);

        if (failure is null) return result;
        int? status = failure is ClientResultException cre ? cre.Status : null;
        bool retryable = status is { } s ? ModelCallException.IsRetryableStatus(s) : !cancelled;
        throw new ModelCallException(
            cancelled ? "model call cancelled" : $"model call failed: {failure.Message}",
            result, retryable, status, cancelled, failure);
    }

    // The two protocols name the same outcomes differently; the turn reads one vocabulary.
    static string MapFinish(string finish) => finish switch
    {
        "toolcalls" or "tool_calls" => "tool_use",
        "stop" => "end_turn",
        "length" => "max_tokens",
        _ => finish,
    };

    static List<ChatMessage> ToOpenAIMessages(ModelRequest req)
    {
        var out_ = new List<ChatMessage>(req.Messages.Count + 1);
        if (req.System.Length > 0) out_.Add(new SystemChatMessage(req.System));
        foreach (var m in req.Messages)
        {
            if (m.Role == Role.Assistant)
            {
                if (m.ToolCalls is { Count: > 0 } tcs)
                {
                    var assistant = new AssistantChatMessage(tcs.Select(c =>
                        ChatToolCall.CreateFunctionToolCall(c.Id, c.Name, BinaryData.FromString(c.Arguments.GetRawText()))));
                    if (m.Text.Length > 0) assistant.Content.Add(ChatMessageContentPart.CreateTextPart(m.Text));
                    out_.Add(assistant);
                    continue;
                }
                if (m.Text.Length > 0) out_.Add(new AssistantChatMessage(m.Text));
                continue;
            }
            // Tool results are their own role on this protocol, one message each, rather
            // than blocks inside a user message as on Anthropic's.
            foreach (var r in m.ToolResults ?? [])
                out_.Add(new ToolChatMessage(r.CallId, r.Content));
            if (m.Text.Length > 0) out_.Add(new UserChatMessage(m.Text));
        }
        return out_;
    }
}
