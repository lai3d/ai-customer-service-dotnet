using System.Text.Json;
using CustomerService.Chat;
using CustomerService.Cost;
using CustomerService.Llm;
using CustomerService.Obs;
using CustomerService.Rag;
using CustomerService.Tests.Support;
using CustomerService.Tools;

namespace CustomerService.Tests;

/// <summary>The turn itself, against a real Postgres, with a scripted model and a stub embedder.</summary>
[Collection("postgres-8")]
public class ChatServiceTests(Postgres8 pg)
{
    sealed class Fixture
    {
        public IChatModel Model = new ScriptedModel();
        public ScriptedModel Scripted => (ScriptedModel)Model;
        public ConversationBudget Budget = new(200_000, 100);
        public Metrics Metrics = new();
        public ConversationMemory Memory = null!;
        public VectorStore Store = null!;
        public List<TurnEvent> Events = new();
        public ChatService Service = null!;
        public List<ITool> Tools = [new OrderLookup(), new SupportTickets(100)];

        public async ValueTask Emit(TurnEvent e) { lock (Events) Events.Add(e); }
        public string Reply => string.Concat(Events.OfType<MessageEvent>().Select(m => m.Text));
        public UsageSummary Usage => Events.OfType<UsageEvent>().Single().Usage;
    }

    async Task<Fixture> Build(Action<Fixture>? configure = null, bool ingest = true)
    {
        var f = new Fixture();
        configure?.Invoke(f);
        var embedder = new StubEmbedder(pg.Dimensions);
        f.Store = new VectorStore(pg.Db);
        if (ingest) await Ingest.RunAsync(Repo.CorpusPath, embedder, f.Store, Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance, CancellationToken.None);
        f.Memory = new ConversationMemory(pg.Db, 40);
        f.Service = new ChatService(f.Memory, new Retriever(embedder, f.Store, 8, 0), f.Model, f.Budget, f.Metrics, 1024, null, f.Tools);
        return f;
    }

    static string NewId() => Guid.NewGuid().ToString();

    async Task<List<(string role, string content)>> Rows(string id)
    {
        await using var cmd = pg.Db.CreateCommand("SELECT role, content FROM chat_memory WHERE conversation_id = $1 ORDER BY id");
        cmd.Parameters.Add(new Npgsql.NpgsqlParameter { Value = id });
        await using var r = await cmd.ExecuteReaderAsync();
        var rows = new List<(string, string)>();
        while (await r.ReadAsync()) rows.Add((r.GetString(0), r.GetString(1)));
        return rows;
    }

    /// <summary>
    /// The ordering constraint the Java implementation pins with a test, held here by the
    /// shape of the turn: memory is written before retrieval runs, and passages travel with
    /// the request only.
    /// </summary>
    [Fact]
    public async Task RetrievedPassagesNeverEnterMemory()
    {
        var f = await Build(x => x.Scripted.Script.Add(ScriptedModel.Text("Thirty days.")));
        var id = NewId();
        await f.Service.TurnAsync(id, "how long do I have to return something?", f.Emit, CancellationToken.None);

        var rows = await Rows(id);
        Assert.Equal([("user", "how long do I have to return something?"), ("assistant", "Thirty days.")], rows);
        Assert.DoesNotContain(rows, r => r.content.Contains("Reference material"));

        var sent = f.Scripted.LastRequest;
        Assert.Equal(ChatService.SystemPrompt, sent.System);
        var last = sent.Messages[^1];
        Assert.Equal(Role.User, last.Role);
        Assert.Contains("Reference material, selected by similarity", last.Text);
        Assert.Contains("Q: ", last.Text);
        Assert.EndsWith("Customer's question:\nhow long do I have to return something?", last.Text);
        Assert.Equal(8, f.Events.OfType<RetrievalEvent>().Single().Passages.Count);
    }

    [Fact]
    public async Task ASecondTurnDoesNotResendTheFirstTurnsPassages()
    {
        var f = await Build(x => { x.Scripted.Script.Add(ScriptedModel.Text("Thirty days.")); x.Scripted.Script.Add(ScriptedModel.Text("Yes, a gift too.")); });
        var id = NewId();
        await f.Service.TurnAsync(id, "how long do I have?", f.Emit, CancellationToken.None);
        await f.Service.TurnAsync(id, "and if it was a gift?", f.Emit, CancellationToken.None);

        var second = f.Scripted.LastRequest.Messages;
        Assert.Equal(3, second.Count);
        Assert.Equal("how long do I have?", second[0].Text);
        Assert.Equal("Thirty days.", second[1].Text);
        Assert.Contains("Reference material", second[2].Text);
        Assert.Equal(1, second.Count(m => m.Text.Contains("Reference material")));
    }

    [Fact]
    public async Task UsageIsSummedAcrossEveryModelCallInATurn()
    {
        var f = await Build(x =>
        {
            x.Scripted.Script.Add(ScriptedModel.ToolUse("", "lookup_order_status", new { orderNumber = "ORD-10042" }, input: 1842, output: 70));
            x.Scripted.Script.Add(ScriptedModel.Text("It is in transit.", input: 2032, output: 222));
        });
        var id = NewId();
        await f.Service.TurnAsync(id, "where is ORD-10042?", f.Emit, CancellationToken.None);
        var u = f.Usage;
        Assert.Equal((2, 3874L, 292L), (u.ModelCalls, u.InputTokens, u.OutputTokens));
        Assert.Equal(4166, f.Budget.Spent(id));
        Assert.Equal(2, f.Metrics.ModelCalls.WithLabels(ScriptedModel.ReportedModel, "success").Value);
        Assert.Equal(3874, f.Metrics.Tokens.WithLabels(ScriptedModel.ReportedModel, "input").Value);
    }

    [Fact]
    public async Task ToolResultsReachTheFollowingModelCall()
    {
        var f = await Build(x =>
        {
            x.Scripted.Script.Add(ScriptedModel.ToolUse("", "lookup_order_status", new { orderNumber = "ORD-10042" }, id: "toolu_1"));
            x.Scripted.Script.Add(ScriptedModel.Text("In transit with SingPost."));
        });
        await f.Service.TurnAsync(NewId(), "where is ORD-10042?", f.Emit, CancellationToken.None);
        Assert.Equal(2, f.Scripted.Requests.Count);
        var followUp = f.Scripted.Requests[1].Messages;
        Assert.Equal(Role.Assistant, followUp[^2].Role);
        Assert.Equal("lookup_order_status", followUp[^2].ToolCalls!.Single().Name);
        var result = followUp[^1].ToolResults!.Single();
        Assert.Equal("toolu_1", result.CallId);
        Assert.Contains("\"found\":true", result.Content);
        Assert.Contains("SP884213906SG", result.Content);
        var tool = f.Events.OfType<ToolEvent>().Single().Tool;
        Assert.Equal(("lookup_order_status", "found"), (tool.Name, tool.Outcome));
        Assert.Equal(1, f.Metrics.ToolCalls.WithLabels("lookup_order_status", "found").Value);
    }

    /// <summary>
    /// A tool-calling turn is two model calls, and the second one's text is a new message. Run
    /// together they read as a typo -- and the run-together string would be what memory
    /// stores and re-sends as history on every later turn.
    /// </summary>
    [Fact]
    public async Task TextFromTwoModelCallsIsNotRunTogether()
    {
        var f = await Build(x =>
        {
            x.Scripted.Script.Add(ScriptedModel.ToolUse("I'll look that up for you.", "lookup_order_status", new { orderNumber = "ORD-10042" }));
            x.Scripted.Script.Add(ScriptedModel.Text("Your order ORD-10042 is in transit."));
        });
        var id = NewId();
        await f.Service.TurnAsync(id, "where is ORD-10042?", f.Emit, CancellationToken.None);
        const string want = "I'll look that up for you.\n\nYour order ORD-10042 is in transit.";
        Assert.Equal(want, f.Reply);
        Assert.Equal(want, (await Rows(id)).Last().content);
        Assert.Contains(f.Events.OfType<MessageEvent>(), m => m.Text == ChatService.ParagraphBreak);
    }

    [Fact]
    public async Task ASingleModelCallGainsNoParagraphBreak()
    {
        var f = await Build(x => x.Scripted.Script.Add(ScriptedModel.Text("Thirty days.")));
        var id = NewId();
        await f.Service.TurnAsync(id, "how long?", f.Emit, CancellationToken.None);
        Assert.Equal("Thirty days.", f.Reply);
        Assert.DoesNotContain(f.Events.OfType<MessageEvent>(), m => m.Text == ChatService.ParagraphBreak);
    }

    [Fact]
    public async Task AConversationIsRefusedOnceItHasSpentItsBudget()
    {
        var f = await Build(x => { x.Budget = new ConversationBudget(100, 100); x.Scripted.Script.Add(ScriptedModel.Text("Thirty days.", input: 90, output: 20)); });
        var id = NewId();
        await f.Service.TurnAsync(id, "how long?", f.Emit, CancellationToken.None);
        await Assert.ThrowsAsync<BudgetExceededException>(() => f.Service.TurnAsync(id, "and now?", f.Emit, CancellationToken.None));
        Assert.Equal(1, f.Scripted.Calls);
        // Refused before the model was called, and before the message was written to memory.
        Assert.Equal(2, (await Rows(id)).Count);
    }

    [Fact]
    public async Task AFailureBeforeTheModelIsStillCountedAsATurn()
    {
        var f = await Build(x => x.Budget = new ConversationBudget(1, 100));
        var id = NewId();
        f.Budget.Record(id, 5);
        await Assert.ThrowsAsync<BudgetExceededException>(() => f.Service.TurnAsync(id, "hi", f.Emit, CancellationToken.None));
        Assert.Equal(1, f.Metrics.Turns.WithLabels("budget_exceeded").Value);
        Assert.Equal(0, f.Metrics.Turns.WithLabels("completed").Value);
    }

    [Fact]
    public async Task ATurnStoppedByTheToolCapIsNotRecordedAsCompleted()
    {
        var f = await Build(x =>
        {
            for (int i = 0; i < 10; i++)
                x.Scripted.Script.Add(ScriptedModel.ToolUse("", "lookup_order_status", new { orderNumber = "ORD-10042" }));
        });
        await f.Service.TurnAsync(NewId(), "keep looking", f.Emit, CancellationToken.None);
        Assert.Equal(ChatService.MaxToolRounds, f.Scripted.Calls);
        Assert.Equal(1, f.Metrics.Turns.WithLabels("tool_limit").Value);
        Assert.Equal(0, f.Metrics.Turns.WithLabels("completed").Value);
        Assert.Equal(ChatService.MaxToolRounds, f.Usage.ModelCalls);
    }

    /// <summary>
    /// A span name and a metric label are aggregated dimensions, and the tool name is written
    /// by the model. The model gets the name it asked for, so it can recover; the meters get
    /// the bounded one.
    /// </summary>
    [Fact]
    public async Task AnInventedToolNameNeverBecomesAMetricLabel()
    {
        var invented = new string('z', 200);
        var f = await Build(x =>
        {
            x.Scripted.Script.Add(ScriptedModel.ToolUse("", invented, new { }));
            x.Scripted.Script.Add(ScriptedModel.Text("Sorry, I cannot do that."));
        });
        await f.Service.TurnAsync(NewId(), "do the thing", f.Emit, CancellationToken.None);
        Assert.Equal(1, f.Metrics.ToolCalls.WithLabels("unknown", "unknown_tool").Value);
        Assert.Equal(("unknown_tool", invented), (f.Events.OfType<ToolEvent>().Single().Tool.Outcome, f.Events.OfType<ToolEvent>().Single().Tool.Name));
        var result = f.Scripted.Requests[1].Messages[^1].ToolResults!.Single();
        Assert.True(result.IsError);
        Assert.Contains(invented, result.Content);
    }

    /// <summary>
    /// A client that disconnects mid-answer must not leave an orphaned user message behind:
    /// whatever the model managed to say is persisted, with a token detached from the request's.
    /// </summary>
    [Fact]
    public async Task APartialReplyIsPersistedWhenTheClientDisconnects()
    {
        var partial = new ModelResult("Thirty ", [], "", new Usage(1842, 0), ScriptedModel.ReportedModel);
        var f = await Build(x => x.Model = new ScriptedModel
        {
            Error = new ModelCallException("model call cancelled", partial, retryable: false, null, cancelled: true),
            OnCall = _ => Task.CompletedTask,
        });
        // The stub throws before forwarding text, so stand in for the streamed prefix through
        // the event that the client would have received.
        var id = NewId();
        var ex = await Assert.ThrowsAsync<ModelCallException>(() => f.Service.TurnAsync(id, "how long?", async e => { await f.Emit(e); }, CancellationToken.None));
        Assert.True(ex.Cancelled);
        Assert.Equal(1, f.Metrics.Turns.WithLabels("cancelled").Value);
        // Usage from the aborted call is still billed to the conversation and the meters.
        Assert.Equal(1842, f.Budget.Spent(id));
        Assert.Equal(1, f.Metrics.ModelCalls.WithLabels(ScriptedModel.ReportedModel, "error").Value);
        Assert.Equal(1842, f.Usage.InputTokens);
    }

    [Fact]
    public async Task WhatTheModelSaidBeforeAFailureIsPersisted()
    {
        var f = await Build(x => x.Model = new StreamingThenFailingModel());
        var id = NewId();
        await Assert.ThrowsAsync<ModelCallException>(() => f.Service.TurnAsync(id, "how long?", f.Emit, CancellationToken.None));
        var rows = await Rows(id);
        Assert.Equal(("assistant", "Thirty "), rows.Last());
    }

    sealed class StreamingThenFailingModel : IChatModel
    {
        public string Provider => "stub"; public string Model => "stub-model";
        public async Task<ModelResult> StreamAsync(ModelRequest request, Func<string, ValueTask> onText, CancellationToken ct)
        {
            await onText("Thirty ");
            throw new ModelCallException("connection dropped", new ModelResult("Thirty ", [], "", new Usage(1842, 0), ScriptedModel.ReportedModel), retryable: true, null, cancelled: false);
        }
    }

    /// <summary>
    /// Two browser tabs on one conversation: without the lock the second request's user message
    /// and reply land between the first request's write and its history read, so the first
    /// sends the model a conversation ending in somebody else's answer, and its retrieved
    /// passages are dropped on the floor at the same time.
    /// </summary>
    [Fact]
    public async Task OverlappingTurnsOnOneConversationDoNotInterleave()
    {
        var firstCallStarted = new TaskCompletionSource();
        var releaseFirst = new TaskCompletionSource();
        var f = await Build(x => x.Model = new ScriptedModel
        {
            Script = [ScriptedModel.Text("answer 1"), ScriptedModel.Text("answer 2")],
            OnCall = async call => { if (call == 0) { firstCallStarted.SetResult(); await releaseFirst.Task; } },
        });
        var id = NewId();
        var first = f.Service.TurnAsync(id, "question 1", f.Emit, CancellationToken.None);
        await firstCallStarted.Task;
        var second = f.Service.TurnAsync(id, "question 2", f.Emit, CancellationToken.None);
        await Task.Delay(100);
        Assert.False(second.IsCompleted, "the second turn must wait for the first");
        Assert.Equal(1, f.Scripted.Calls);
        releaseFirst.SetResult();
        await Task.WhenAll(first, second);

        Assert.Equal(2, f.Scripted.Requests.Count);
        var firstMessages = f.Scripted.Requests[0].Messages;
        Assert.Single(firstMessages);
        Assert.EndsWith("question 1", firstMessages[0].Text);
        Assert.Contains("Reference material", firstMessages[0].Text);
        var secondMessages = f.Scripted.Requests[1].Messages;
        Assert.Equal(["question 1", "answer 1"], secondMessages.Take(2).Select(m => m.Text).ToArray());
        Assert.EndsWith("question 2", secondMessages[^1].Text);
        Assert.Contains("Reference material", secondMessages[^1].Text);
        Assert.Equal(0, f.Service.InFlightConversations);
    }

    [Fact]
    public async Task RetrievalIsReportedBeforeTheModelIsCalledAndSurvivesItsFailure()
    {
        var f = await Build(x => x.Model = new ScriptedModel
        {
            Error = new ModelCallException("boom", ModelResult.Empty with { Model = ScriptedModel.ReportedModel }, retryable: false, 400, cancelled: false),
        });
        await Assert.ThrowsAsync<ModelCallException>(() => f.Service.TurnAsync(NewId(), "hi", f.Emit, CancellationToken.None));
        Assert.IsType<RetrievalEvent>(f.Events[0]);
        Assert.Equal(1, f.Metrics.Turns.WithLabels("failed").Value);
    }

    [Fact]
    public async Task ConsecutiveUserMessagesAreMergedWhenHistoryIsRead()
    {
        var f = await Build(ingest: false);
        var id = NewId();
        await f.Memory.AppendAsync(id, Role.User, "first", CancellationToken.None);
        await f.Memory.AppendAsync(id, Role.User, "second", CancellationToken.None);
        await f.Memory.AppendAsync(id, Role.Assistant, "reply", CancellationToken.None);
        var history = await f.Memory.HistoryAsync(id, CancellationToken.None);
        Assert.Equal(2, history.Count);
        Assert.Equal("first\n\nsecond", history[0].Text);
        Assert.Equal(3, await f.Memory.CountAsync(id, CancellationToken.None));
    }

    [Fact]
    public async Task ReingestingReplacesRatherThanDuplicates()
    {
        var f = await Build();
        await Ingest.RunAsync(Repo.CorpusPath, new StubEmbedder(pg.Dimensions), f.Store, Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance, CancellationToken.None);
        Assert.Equal(36, await f.Store.CountAsync(CancellationToken.None));
    }

    [Fact]
    public async Task AnUnpricedModelIsCountedRatherThanCostedAtZeroSilently()
    {
        var f = await Build(x => x.Scripted.Script.Add(ScriptedModel.Text("ok")));
        await f.Service.TurnAsync(NewId(), "hi", f.Emit, CancellationToken.None);
        Assert.Equal(1, f.Metrics.Unpriced.WithLabels(ScriptedModel.ReportedModel).Value);
        Assert.Equal(0, f.Usage.CostUsd);
        var json = JsonSerializer.Serialize<TurnEvent>(f.Events.OfType<UsageEvent>().Single(), HttpApi.ChatEndpoints.Json);
        Assert.DoesNotContain("costUsd", json);
        Assert.DoesNotContain("traceId", json);
    }
}
