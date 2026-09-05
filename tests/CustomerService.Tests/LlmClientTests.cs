using System.Text.Json;
using CustomerService.Llm;
using CustomerService.Tests.Support;
using A = CustomerService.Tests.Support.FakeProvider.Anthropic;
using O = CustomerService.Tests.Support.FakeProvider.OpenAI;

namespace CustomerService.Tests;

/// <summary>
/// These tests drive the real clients against a fake provider, because the thing being
/// asserted is a property of the clients and cannot be checked above them.
/// </summary>
public class LlmClientTests
{
    static ModelOptions Options(FakeProvider fake, string model = "claude-opus-5") =>
        new("test-key", model, "http://fake.provider.test/v1", 1024, MaxAttempts: 1, TimeSpan.FromSeconds(5), fake.Client());

    static ModelRequest Request(params ModelMessage[] messages) =>
        new("you are a test", messages.Length == 0 ? [new ModelMessage(Role.User, "how long do I have?")] : messages,
            [new OrderLookupDefinition().Definition], 1024);

    sealed class OrderLookupDefinition { public ToolDefinition Definition => new Tools.OrderLookup().Definition; }

    static async Task<string> Collect(IChatModel model, ModelRequest request, List<string>? into = null, CancellationToken ct = default)
    {
        var text = into ?? new();
        var r = await model.StreamAsync(request, t => { text.Add(t); return ValueTask.CompletedTask; }, ct);
        return r.Text;
    }

    // ---- what a complete stream yields -------------------------------------------------

    [Fact]
    public async Task AnthropicToolCallsAndUsageAreReadFromTheStream()
    {
        var fake = FakeProvider.Sse(A.MessageStart(), A.TextBlockStart(), A.TextDelta("I'll look "), A.TextDelta("that up."), A.BlockStop(0),
            A.ToolUseStart("toolu_1", "lookup_order_status"), A.InputJsonDelta("{\"orderNum"), A.InputJsonDelta("ber\":\"ORD-10042\"}"), A.BlockStop(1),
            A.MessageDelta("tool_use", 70), A.MessageStop);
        var model = new AnthropicChatModel(Options(fake));
        var chunks = new List<string>();
        var result = await model.StreamAsync(Request(), t => { chunks.Add(t); return ValueTask.CompletedTask; }, CancellationToken.None);

        Assert.Equal("I'll look that up.", result.Text);
        Assert.Equal(["I'll look ", "that up."], chunks);
        Assert.Equal("tool_use", result.StopReason);
        Assert.Equal(new Usage(A.InputTokensOnTheWire, 70), result.Usage);
        Assert.Equal("claude-opus-5", result.Model);
        var call = Assert.Single(result.ToolCalls);
        Assert.Equal(("toolu_1", "lookup_order_status", "ORD-10042"), (call.Id, call.Name, call.Arguments.GetProperty("orderNumber").GetString()));
        Assert.NotNull(result.Native);
    }

    [Fact]
    public async Task TheOpenAIProtocolToolCallsAndUsageAreReadFromTheStream()
    {
        var fake = FakeProvider.Sse(O.RoleChunk(), O.ToolCallChunk(0, "call_1", "lookup_order_status", ""), O.ToolCallChunk(0, null, null, "{\"orderNumber\":"),
            O.ToolCallChunk(0, null, null, "\"ORD-10042\"}"), O.Finish("tool_calls"), O.Usage(1029, 220), O.Done);
        var model = OpenAIProtocolChatModel.OpenAI(Options(fake, "gpt-5"));
        var result = await model.StreamAsync(Request(), _ => ValueTask.CompletedTask, CancellationToken.None);

        Assert.Equal("tool_use", result.StopReason);
        Assert.Equal(new Usage(1029, 220), result.Usage);
        Assert.Equal("gpt-5-2025-08-07", result.Model);
        var call = Assert.Single(result.ToolCalls);
        Assert.Equal(("call_1", "lookup_order_status", "ORD-10042"), (call.Id, call.Name, call.Arguments.GetProperty("orderNumber").GetString()));
    }

    // ---- what the clients send ----------------------------------------------------------

    [Fact]
    public async Task NeitherClientSendsASamplingParameter()
    {
        var anthropic = FakeProvider.Sse(A.MessageStart(), A.MessageDelta("end_turn", 1), A.MessageStop);
        await Collect(new AnthropicChatModel(Options(anthropic)), Request());
        var openai = FakeProvider.Sse(O.RoleChunk(), O.Finish("stop"), O.Usage(1, 1), O.Done);
        await Collect(OpenAIProtocolChatModel.OpenAI(Options(openai, "gpt-5")), Request());

        foreach (var body in new[] { anthropic.RequestBodies.Single(), openai.RequestBodies.Single() })
        {
            using var doc = JsonDocument.Parse(body);
            foreach (var forbidden in new[] { "temperature", "top_p", "top_k", "presence_penalty", "frequency_penalty" })
                Assert.False(doc.RootElement.TryGetProperty(forbidden, out _), $"{forbidden} was sent: {body}");
            Assert.True(doc.RootElement.GetProperty("stream").GetBoolean());
        }
        using var a = JsonDocument.Parse(anthropic.RequestBodies.Single());
        Assert.Equal(1024, a.RootElement.GetProperty("max_tokens").GetInt32());
        Assert.Equal("lookup_order_status", a.RootElement.GetProperty("tools")[0].GetProperty("name").GetString());
        Assert.True(a.RootElement.GetProperty("tools")[0].GetProperty("input_schema").TryGetProperty("properties", out _));
    }

    /// <summary>
    /// Without this the response carries no usage at all, and the failure is silent: a
    /// conversation budget built on those numbers never triggers and the cost meters stay at
    /// zero while real money is spent. Anthropic sends usage unasked, which is how this hides.
    /// </summary>
    [Fact]
    public async Task TheOpenAIProtocolAsksForUsageInTheStream()
    {
        var fake = FakeProvider.Sse(O.RoleChunk(), O.Finish("stop"), O.Usage(1, 1), O.Done);
        await Collect(OpenAIProtocolChatModel.XAI(Options(fake, "grok-4.6")), Request());
        using var doc = JsonDocument.Parse(fake.RequestBodies.Single());
        Assert.True(doc.RootElement.GetProperty("stream_options").GetProperty("include_usage").GetBoolean());
        Assert.Equal("lookup_order_status", doc.RootElement.GetProperty("tools")[0].GetProperty("function").GetProperty("name").GetString());
    }

    [Fact]
    public async Task ToolResultsReachTheFollowingModelCallOnBothProtocols()
    {
        var args = JsonSerializer.SerializeToElement(new { orderNumber = "ORD-10042" });
        var history = new ModelMessage[]
        {
            new(Role.User, "where is ORD-10042?"),
            new(Role.Assistant, "Let me check.", [new ToolCall("call_1", "lookup_order_status", args)]),
            new(Role.User, ToolResults: [new ToolResult("call_1", "{\"found\":true}")]),
        };
        var anthropic = FakeProvider.Sse(A.MessageStart(), A.MessageDelta("end_turn", 1), A.MessageStop);
        await Collect(new AnthropicChatModel(Options(anthropic)), Request(history));
        var body = anthropic.RequestBodies.Single();
        Assert.Contains("\"tool_result\"", body);
        Assert.Contains("\"tool_use_id\":\"call_1\"", body);
        Assert.Contains("{\\u0022found\\u0022:true}".Replace("\\u0022", "\\\""), body.Replace("\\u0022", "\\\""));

        var openai = FakeProvider.Sse(O.RoleChunk(), O.Finish("stop"), O.Usage(1, 1), O.Done);
        await Collect(OpenAIProtocolChatModel.OpenAI(Options(openai, "gpt-5")), Request(history));
        using var doc = JsonDocument.Parse(openai.RequestBodies.Single());
        var messages = doc.RootElement.GetProperty("messages").EnumerateArray().ToList();
        Assert.Equal("system", messages[0].GetProperty("role").GetString());
        Assert.Equal("tool", messages[^1].GetProperty("role").GetString());
        Assert.Equal("call_1", messages[^1].GetProperty("tool_call_id").GetString());
        Assert.Equal("call_1", messages[^2].GetProperty("tool_calls")[0].GetProperty("id").GetString());
    }

    // ---- what survives a failure ---------------------------------------------------------

    /// <summary>
    /// Anthropic reports the input count at message_start, before a single token of the
    /// answer, so a stream that dies half-way through has spent real money. The client must
    /// hand that up rather than throw it away.
    /// </summary>
    [Fact]
    public async Task AnthropicKeepsItsUsageWhenTheStreamIsCutOff()
    {
        var fake = FakeProvider.SseThenCutOff(A.MessageStart(), A.TextBlockStart(), A.TextDelta("Thirty "));
        var model = new AnthropicChatModel(Options(fake));
        var chunks = new List<string>();
        var ex = await Assert.ThrowsAsync<ModelCallException>(() => Collect(model, Request(), chunks));
        Assert.Equal(A.InputTokensOnTheWire, ex.Partial.Usage.InputTokens);
        Assert.Equal("claude-opus-5", ex.Partial.Model);
        Assert.Equal(["Thirty "], chunks);
        Assert.False(ex.Cancelled);
        Assert.True(ex.Retryable, "a transport failure is worth another attempt");
        Assert.Null(ex.Partial.Native);
    }

    /// <summary>The client-disconnect path, and the most likely mid-stream failure in production.</summary>
    [Fact]
    public async Task AnthropicKeepsTheUsageItWasAlreadyToldWhenTheConsumerGivesUp()
    {
        var fake = FakeProvider.Sse(A.MessageStart(), A.TextBlockStart(), A.TextDelta("Thirty "), A.TextDelta("days."), A.BlockStop(0), A.MessageDelta("end_turn", 5), A.MessageStop);
        var model = new AnthropicChatModel(Options(fake));
        using var cts = new CancellationTokenSource();
        var ex = await Assert.ThrowsAsync<ModelCallException>(() => model.StreamAsync(Request(), _ =>
        {
            cts.Cancel();
            cts.Token.ThrowIfCancellationRequested();
            return ValueTask.CompletedTask;
        }, cts.Token));
        Assert.True(ex.Cancelled);
        Assert.False(ex.Retryable);
        Assert.Equal(A.InputTokensOnTheWire, ex.Partial.Usage.InputTokens);
    }

    /// <summary>
    /// The asymmetry is a property of the protocol rather than of this code: usage arrives in
    /// a single final chunk, so a call that dies mid-stream genuinely has nothing to report,
    /// and the client returns a zero honestly rather than inventing a number.
    /// </summary>
    [Fact]
    public async Task TheOpenAIProtocolHasNoUsageToKeepWhenAStreamIsAbandoned()
    {
        var fake = FakeProvider.SseThenCutOff(O.RoleChunk(), O.Chunk("Thirty "));
        var model = OpenAIProtocolChatModel.OpenAI(Options(fake, "gpt-5"));
        var chunks = new List<string>();
        var ex = await Assert.ThrowsAsync<ModelCallException>(() => Collect(model, Request(), chunks));
        Assert.Equal(default, ex.Partial.Usage);
        Assert.Equal("gpt-5-2025-08-07", ex.Partial.Model);
        Assert.Equal(["Thirty "], chunks);
    }
}
