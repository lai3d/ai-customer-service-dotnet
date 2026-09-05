using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CustomerService.Chat;
using CustomerService.Config;
using CustomerService.Cost;
using CustomerService.HttpApi;
using CustomerService.Llm;
using CustomerService.Tests.Support;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CustomerService.Tests;

/// <summary>
/// The edge -- validation, status codes, SSE framing -- tested with no database, no model and
/// no embedding model, against a scripted turn.
/// </summary>
public class HttpApiTests
{
    sealed class ScriptedTurner(Func<string, string, Func<TurnEvent, ValueTask>, CancellationToken, Task> turn) : ITurner
    {
        public int Calls;
        public Task TurnAsync(string conversationId, string message, Func<TurnEvent, ValueTask> emit, CancellationToken ct)
        {
            Interlocked.Increment(ref Calls);
            return turn(conversationId, message, emit, ct);
        }
    }

    static readonly ChatConfig Cfg = new("stub", "stub", "k", "", 1024, 40, 64, 100, 1, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1), TimeSpan.FromMilliseconds(40));

    static async Task<(WebApplication app, HttpClient client)> Start(ITurner turner, ChatConfig? cfg = null)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        var app = builder.Build();
        app.MapChatEndpoints(turner, cfg ?? Cfg, NullLogger.Instance);
        app.MapDemoPage();
        await app.StartAsync();
        return (app, app.GetTestClient());
    }

    static ScriptedTurner HappyTurn() => new(async (_, _, emit, _) =>
    {
        await emit(new RetrievalEvent([new PassageSummary("returns-window", "en", 0.91, "How long do I have to return an item?")]));
        await emit(new ToolEvent(new ToolSummary("lookup_order_status", "found")));
        await emit(new MessageEvent("Thirty "));
        await emit(new MessageEvent("days."));
        await emit(new UsageEvent(new UsageSummary("stub-model", 2, 100, 20, 0, 5, null)));
    });

    [Theory]
    [InlineData("{\"message\":\"\"}", "Message required")]
    [InlineData("{\"message\":\"   \"}", "Message required")]
    [InlineData("not json", "Malformed request")]
    public async Task RequestsAreRejectedBeforeAnyModelCall(string body, string title)
    {
        var turner = HappyTurn();
        var (app, client) = await Start(turner);
        await using var running = app;
        var res = await client.PostAsync("/api/v1/chat", new StringContent(body, System.Text.Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        Assert.Equal("application/problem+json", res.Content.Headers.ContentType?.MediaType);
        var problem = await res.Content.ReadFromJsonAsync<Problem>();
        Assert.Equal(title, problem!.Title);
        Assert.Equal(0, turner.Calls);
    }

    [Fact]
    public async Task OversizedMessagesAndConversationIdsAreRejected()
    {
        var turner = HappyTurn();
        var (app, client) = await Start(turner);
        await using var running = app;
        var tooLong = await client.PostAsJsonAsync("/api/v1/chat", new { message = new string('字', 101) });
        Assert.Equal(HttpStatusCode.BadRequest, tooLong.StatusCode);
        var justRight = await client.PostAsJsonAsync("/api/v1/chat", new { message = new string('字', 100) });
        Assert.Equal(HttpStatusCode.OK, justRight.StatusCode);
        var longId = await client.PostAsJsonAsync("/api/v1/chat/stream", new { conversationId = new string('x', 65), message = "hi" });
        Assert.Equal(HttpStatusCode.BadRequest, longId.StatusCode);
        Assert.Equal(1, turner.Calls);
    }

    [Fact]
    public async Task AConversationIdComesBackOnEveryResponse()
    {
        var (app, client) = await Start(HappyTurn());
        await using var running = app;
        var fresh = await client.PostAsJsonAsync("/api/v1/chat", new { message = "hi" });
        var assigned = fresh.Headers.GetValues(ChatEndpoints.ConversationIdHeader).Single();
        Assert.True(Guid.TryParse(assigned, out _));
        var reply = await fresh.Content.ReadFromJsonAsync<ChatReply>(ChatEndpoints.Json);
        Assert.Equal(assigned, reply!.ConversationId);
        Assert.Equal("Thirty days.", reply.Reply);
        Assert.Equal(2, reply.Usage!.ModelCalls);
        Assert.Single(reply.Passages!);
        Assert.Single(reply.Tools!);

        var streamed = await client.PostAsJsonAsync("/api/v1/chat/stream", new { conversationId = "abc-123", message = "hi" });
        Assert.Equal("abc-123", streamed.Headers.GetValues(ChatEndpoints.ConversationIdHeader).Single());
    }

    [Fact]
    public async Task TheStreamCarriesTypedEventsInOrder()
    {
        var turner = HappyTurn();
        var (app, client) = await Start(turner);
        await using var running = app;
        var res = await client.PostAsJsonAsync("/api/v1/chat/stream", new { message = "hi" });
        Assert.Equal("text/event-stream", res.Content.Headers.ContentType?.MediaType);
        var frames = Sse.Parse(await res.Content.ReadAsStringAsync()).Where(f => !f.Comment).ToList();
        Assert.Equal(["retrieval", "tool", "message", "message", "usage"], frames.Select(f => f.Event).ToArray());
        // The payload carries a type too, for clients that keep it, and it agrees with the event name.
        foreach (var f in frames)
            Assert.Equal(f.Event, JsonDocument.Parse(f.Data!).RootElement.GetProperty("type").GetString());
        Assert.Equal("returns-window", JsonDocument.Parse(frames[0].Data!).RootElement.GetProperty("passages")[0].GetProperty("entryId").GetString());
        Assert.Equal(2, JsonDocument.Parse(frames[^1].Data!).RootElement.GetProperty("usage").GetProperty("modelCalls").GetInt32());
        Assert.Equal(1, turner.Calls);
    }

    /// <summary>
    /// A failure after the first token cannot change the status code, so it arrives as a
    /// terminal error event -- named, and carrying problem+json whose `type` is a URI rather
    /// than the string "error", so a page that dispatches on the payload cannot appear to work.
    /// </summary>
    [Fact]
    public async Task AFailureAfterTheFirstTokenArrivesAsAnErrorEvent()
    {
        var turner = new ScriptedTurner(async (_, _, emit, _) =>
        {
            await emit(new MessageEvent("Thirty "));
            throw new BudgetExceededException("c", 200_001, 200_000);
        });
        var (app, client) = await Start(turner);
        await using var running = app;
        var res = await client.PostAsJsonAsync("/api/v1/chat/stream", new { message = "hi" });
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var frames = Sse.Parse(await res.Content.ReadAsStringAsync()).Where(f => !f.Comment).ToList();
        Assert.Equal(["message", "error"], frames.Select(f => f.Event).ToArray());
        var problem = JsonDocument.Parse(frames[1].Data!).RootElement;
        Assert.Equal(429, problem.GetProperty("status").GetInt32());
        Assert.Equal("Conversation budget reached", problem.GetProperty("title").GetString());
        Assert.NotEqual("error", problem.GetProperty("type").GetString());
    }

    [Fact]
    public async Task TheStreamIsKeptAliveWhileTheModelThinks()
    {
        var turner = new ScriptedTurner(async (_, _, emit, ct) =>
        {
            await Task.Delay(250, ct);
            await emit(new MessageEvent("done"));
        });
        var (app, client) = await Start(turner);
        await using var running = app;
        var res = await client.PostAsJsonAsync("/api/v1/chat/stream", new { message = "hi" });
        var frames = Sse.Parse(await res.Content.ReadAsStringAsync());
        Assert.True(frames.Count(f => f.Comment) >= 2, "expected keep-alive comments before the first token");
        Assert.True(frames.FindIndex(f => f.Comment) < frames.FindIndex(f => f.Event == "message"));
    }

    [Fact]
    public async Task FailuresMapToStatusesAClientCanActOn()
    {
        var cases = new (Exception error, int status)[]
        {
            (new BudgetExceededException("c", 1, 1), 429),
            (new ModelCallException("overloaded", ModelResult.Empty, retryable: true, 529, cancelled: false), 503),
            (new ModelCallException("bad request", ModelResult.Empty, retryable: false, 400, cancelled: false), 502),
            (new InvalidOperationException("boom"), 500),
        };
        foreach (var (error, status) in cases)
        {
            var (app, client) = await Start(new ScriptedTurner((_, _, _, _) => throw error));
            await using var running = app;
            var res = await client.PostAsJsonAsync("/api/v1/chat", new { message = "hi" });
            Assert.Equal(status, (int)res.StatusCode);
            Assert.Equal("application/problem+json", res.Content.Headers.ContentType?.MediaType);
        }
    }

    [Fact]
    public async Task TheDemoPageIsServedAtTheRoot()
    {
        var (app, client) = await Start(HappyTurn());
        await using var running = app;
        var res = await client.GetAsync("/");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal("text/html", res.Content.Headers.ContentType?.MediaType);
        Assert.Contains("AI Customer Service · .NET", await res.Content.ReadAsStringAsync());
    }
}

public class DemoPageTests
{
    static readonly string Html = System.Text.Encoding.UTF8.GetString(DemoPage.Html);

    // The page explains itself in comments, and a comment that names a sink is not a use of
    // it. Assertions run against the code alone.
    static readonly string Code = System.Text.RegularExpressions.Regex.Replace(
        System.Text.RegularExpressions.Regex.Replace(Html, "<!--.*?-->", "", System.Text.RegularExpressions.RegexOptions.Singleline),
        @"^\s*//.*$", "", System.Text.RegularExpressions.RegexOptions.Multiline);

    /// <summary>
    /// Every sink that turns a string into markup must stay absent. Model text becomes a text
    /// node or it does not appear -- which matters more here than in an ordinary chat client,
    /// because the model's input includes retrieved passages.
    /// </summary>
    [Fact]
    public void TheDemoPageNeverTurnsAStringIntoMarkup()
    {
        foreach (var sink in new[] { "innerHTML", "outerHTML", "insertAdjacentHTML", "document.write", "DOMParser", "createContextualFragment" })
            Assert.DoesNotContain(sink, Code);
        Assert.Contains("textContent", Code);
    }

    /// <summary>
    /// Chat events carry a `type`; a post-commit failure carries problem+json whose `type` is a
    /// URI. Switching on the payload silently drops every error.
    /// </summary>
    [Fact]
    public void TheDemoPageDispatchesOnTheEventName()
    {
        Assert.Contains("startsWith('event: ')", Html);
        Assert.Contains("name === 'error'", Html);
        Assert.DoesNotContain("payload.type ===", Html);
        Assert.DoesNotContain("switch (payload.type)", Html);
    }

    [Fact]
    public void TheRendererDoesNotBuildLinks()
    {
        // Bold, lists and code are rendered; a model-authored href is the one construct that
        // does something rather than looks like something.
        Assert.Contains("function renderMarkdown", Html);
        Assert.DoesNotContain("](", Html.Split("function renderMarkdown")[1].Split("function card")[0]);
        Assert.Contains("open this turn in Jaeger", Html); // the one link, written by the page, not the model
        Assert.Contains("localhost:16688", Html);
    }

    [Fact]
    public void TheFaviconIsInlineSoTheBrowserAsksForNothing()
    {
        Assert.Contains("rel=\"icon\" href=\"data:image/svg+xml", Code);
        Assert.DoesNotContain("favicon.ico", Code);
    }
}
