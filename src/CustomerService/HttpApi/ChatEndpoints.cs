using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;
using CustomerService.Chat;
using CustomerService.Config;
using CustomerService.Cost;
using CustomerService.Llm;
using Microsoft.AspNetCore.Http.Features;

namespace CustomerService.HttpApi;

public sealed record ChatRequest(string? ConversationId, string? Message);

public sealed record ChatReply(
    string ConversationId,
    string Reply,
    IReadOnlyList<PassageSummary>? Passages,
    IReadOnlyList<ToolSummary>? Tools,
    UsageSummary? Usage);

/// <summary>RFC 9457. A client should be able to tell "try again" from "this will never work" without parsing prose.</summary>
public sealed record Problem(string Type, string Title, int Status, string? Detail = null);

/// <summary>
/// The edge: validation, SSE, and turning failures into responses a client can act on.
/// </summary>
public static class ChatEndpoints
{
    /// <summary>Carries the id of the conversation a response belongs to, so a client that omitted one knows what to send next.</summary>
    public const string ConversationIdHeader = "X-Conversation-Id";

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static void MapChatEndpoints(this IEndpointRouteBuilder app, ITurner turner, ChatConfig cfg, ILogger logger)
    {
        app.MapPost("/api/v1/chat", (HttpContext http, CancellationToken ct) => HandleChatAsync(http, turner, cfg, logger, ct));
        app.MapPost("/api/v1/chat/stream", (HttpContext http, CancellationToken ct) => HandleStreamAsync(http, turner, cfg, logger, ct));
    }

    static async Task<(string? message, string? id, Problem? problem)> ReadAndValidateAsync(HttpContext http, ChatConfig cfg, CancellationToken ct)
    {
        http.Features.Get<IHttpMaxRequestBodySizeFeature>()?.MaxRequestBodySize = 1 << 20;
        ChatRequest? req;
        try { req = await JsonSerializer.DeserializeAsync<ChatRequest>(http.Request.Body, Json, ct); }
        catch (JsonException) { req = null; }
        if (req is null)
            return (null, null, new Problem("about:blank", "Malformed request", 400, "The request body is not valid JSON."));

        // Both limits cost nothing to enforce here and are a 500 from the database if they are not.
        var message = (req.Message ?? "").Trim();
        if (message.Length == 0)
            return (null, null, new Problem("about:blank", "Message required", 400, "The message must not be blank."));
        if (message.EnumerateRunes().Count() > cfg.MaxMessageLength)
            return (null, null, new Problem("about:blank", "Message too long", 400, "The message is longer than this service accepts."));
        var id = req.ConversationId ?? "";
        if (id.Length > cfg.MaxConversationIdLength)
            // A client-supplied id lands in a bounded column. Unvalidated, this surfaced as a
            // 500 from a constraint violation in the Java implementation.
            return (null, null, new Problem("about:blank", "Conversation id too long", 400, "The conversation id is longer than this service accepts."));
        return (message, id.Length > 0 ? id : Guid.NewGuid().ToString(), null);
    }

    static async Task HandleChatAsync(HttpContext http, ITurner turner, ChatConfig cfg, ILogger logger, CancellationToken ct)
    {
        var (message, id, problem) = await ReadAndValidateAsync(http, cfg, ct);
        if (problem is not null) { await WriteProblemAsync(http, problem, ct); return; }
        http.Response.Headers[ConversationIdHeader] = id;

        var text = new StringBuilder();
        IReadOnlyList<PassageSummary>? passages = null;
        var tools = new List<ToolSummary>();
        UsageSummary? usage = null;
        var gate = new object();
        try
        {
            await turner.TurnAsync(id!, message!, e =>
            {
                lock (gate)
                {
                    switch (e)
                    {
                        case MessageEvent m: text.Append(m.Text); break;
                        case RetrievalEvent r: passages = r.Passages; break;
                        case ToolEvent t: tools.Add(t.Tool); break;
                        // Recorded here as well as in the meters. The Java implementation's
                        // blocking endpoint threw the response metadata away and with it the
                        // token usage, so that path was invisible to the budget and the cost
                        // meters while spending real money.
                        case UsageEvent u: usage = u.Usage; break;
                    }
                }
                return ValueTask.CompletedTask;
            }, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return;
        }
        catch (Exception ex)
        {
            await WriteProblemAsync(http, ProblemFor(ex, logger), ct);
            return;
        }
        http.Response.ContentType = "application/json; charset=utf-8";
        await JsonSerializer.SerializeAsync(http.Response.Body,
            new ChatReply(id!, text.ToString(), passages, tools.Count > 0 ? tools : null, usage), Json, ct);
    }

    /// <summary>
    /// The streaming endpoint. The turn runs as its own task and events arrive on a channel,
    /// so the heartbeat can interleave with them and the response has exactly one writer.
    /// That the upstream is consumed exactly once is a property of the channel rather than
    /// something to assert: the Java implementation merged a heartbeat into a reactive
    /// stream, where subscribing twice would have run the entire turn twice -- two model
    /// calls, two bills -- while the response still looked correct.
    /// </summary>
    static async Task HandleStreamAsync(HttpContext http, ITurner turner, ChatConfig cfg, ILogger logger, CancellationToken ct)
    {
        var (message, id, problem) = await ReadAndValidateAsync(http, cfg, ct);
        if (problem is not null) { await WriteProblemAsync(http, problem, ct); return; }

        http.Response.StatusCode = 200;
        http.Response.ContentType = "text/event-stream";
        http.Response.Headers.CacheControl = "no-cache";
        http.Response.Headers.Connection = "keep-alive";
        http.Response.Headers[ConversationIdHeader] = id;
        http.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();
        await http.Response.Body.FlushAsync(ct);

        var frames = Channel.CreateUnbounded<Frame>(new UnboundedChannelOptions { SingleReader = true });
        using var turnDone = CancellationTokenSource.CreateLinkedTokenSource(ct);

        var turn = Task.Run(async () =>
        {
            try
            {
                await turner.TurnAsync(id!, message!, e => frames.Writer.WriteAsync(new Frame(e.Name, e), ct), ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
            catch (Exception ex)
            {
                // A failure after the response is committed cannot change the status code, so
                // it arrives as a terminal error event. A client never has to guess whether an
                // apology came from the model or from the transport.
                await frames.Writer.WriteAsync(new Frame("error", ProblemFor(ex, logger)), CancellationToken.None);
            }
            finally
            {
                frames.Writer.TryComplete();
                turnDone.Cancel();
            }
        }, CancellationToken.None);

        // SSE connections are legitimately idle between the request and the first token --
        // retrieval plus a slow model is several seconds -- and proxies close idle
        // connections. A comment-only frame is invisible to any correct client.
        var heartbeat = Task.Run(async () =>
        {
            using var timer = new PeriodicTimer(cfg.KeepAliveInterval);
            try
            {
                while (await timer.WaitForNextTickAsync(turnDone.Token))
                    frames.Writer.TryWrite(Frame.KeepAlive);
            }
            catch (OperationCanceledException) { }
        }, CancellationToken.None);

        try
        {
            await foreach (var frame in frames.Reader.ReadAllAsync(ct))
            {
                if (frame.IsKeepAlive) await http.Response.WriteAsync(": keep-alive\n\n", ct);
                else await http.Response.WriteAsync(Encode(frame), ct);
                await http.Response.Body.FlushAsync(ct);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // The client is gone. Nothing can be sent, and the turn's own persistence has
            // already taken care of the partial reply.
        }
        finally
        {
            turnDone.Cancel();
            await Task.WhenAll(turn, heartbeat);
        }
    }

    sealed record Frame(string Name, object? Payload)
    {
        public static readonly Frame KeepAlive = new("", null);
        public bool IsKeepAlive => Name.Length == 0;
    }

    static string Encode(Frame frame)
    {
        var data = frame.Payload is TurnEvent e
            ? JsonSerializer.Serialize<TurnEvent>(e, Json)
            : JsonSerializer.Serialize(frame.Payload, Json);
        return $"event: {frame.Name}\ndata: {data}\n\n";
    }

    /// <summary>Maps a failure to a response a client can act on: retry, do not retry, or this conversation is over.</summary>
    public static Problem ProblemFor(Exception ex, ILogger logger)
    {
        switch (ex)
        {
            case BudgetExceededException:
                return new Problem("about:blank", "Conversation budget reached", 429,
                    "This conversation has reached its token budget. A human agent can take it from here.");
            case ModelCallException { Retryable: true }:
                return new Problem("about:blank", "The assistant is temporarily unavailable", 503,
                    "The model provider is rate limiting or overloaded. Retrying shortly is worthwhile.");
            case ModelCallException:
                return new Problem("about:blank", "The assistant could not answer", 502,
                    "The model provider rejected the request. Retrying will not help.");
            default:
                logger.LogError(ex, "unhandled failure in a chat turn");
                return new Problem("about:blank", "Internal error", 500);
        }
    }

    static async Task WriteProblemAsync(HttpContext http, Problem p, CancellationToken ct)
    {
        http.Response.StatusCode = p.Status;
        http.Response.ContentType = "application/problem+json; charset=utf-8";
        await JsonSerializer.SerializeAsync(http.Response.Body, p, Json, ct);
    }
}
