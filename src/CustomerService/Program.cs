// The AI customer service backend: wiring, health, graceful shutdown.
using System.Net;
using CustomerService.Admin;
using CustomerService.Chat;
using CustomerService.Config;
using CustomerService.Cost;
using CustomerService.HttpApi;
using CustomerService.Llm;
using CustomerService.Obs;
using CustomerService.Rag;
using CustomerService.Store;
using CustomerService.Tickets;
using CustomerService.Tools;
using OpenTelemetry.Exporter;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Prometheus;

// A container healthcheck without adding curl to the image.
if (args.Length > 0 && args[0] == "--healthcheck")
    return await Healthcheck();

AppConfig cfg;
try
{
    // Configuration failures -- a missing API key in particular -- stop the process here. A
    // service that starts, reports itself healthy, is marked ready by Kubernetes and then
    // 401s every customer request is the worse failure.
    cfg = AppConfig.Load();
}
catch (ConfigException ex)
{
    Console.Error.WriteLine($"startup failed: {ex.Message}");
    return 1;
}

var builder = WebApplication.CreateBuilder(args);
builder.Logging.ClearProviders();
builder.Logging.AddSimpleConsole(o => { o.SingleLine = true; o.TimestampFormat = "HH:mm:ss "; o.UseUtcTimestamp = true; });
// Per-request lines from the framework are noise next to what the turn logs; failures still
// come through at Warning and above.
builder.Logging.AddFilter("Microsoft.AspNetCore", LogLevel.Warning);
builder.Logging.AddFilter("Microsoft.Hosting", LogLevel.Information);
if (Enum.TryParse<LogLevel>(Environment.GetEnvironmentVariable("LOG_LEVEL"), true, out var level))
    builder.Logging.SetMinimumLevel(level);

var (address, port) = ParseAddr(cfg.HttpAddr);
builder.WebHost.ConfigureKestrel(k =>
{
    k.Listen(address, port);
    // No response write timeout: an SSE response is legitimately open for as long as the
    // model keeps talking. The request-side timeouts still bound how long a client may
    // take to send a request.
    k.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(10);
    k.Limits.KeepAliveTimeout = TimeSpan.FromSeconds(120);
});
// Stop accepting, let in-flight turns finish. This has to stay below the pod's
// terminationGracePeriodSeconds, or the container is killed part-way through the grace
// period it was given.
builder.Host.ConfigureHostOptions(o => o.ShutdownTimeout = cfg.ShutdownTimeout);

if (cfg.Obs.OtlpEnabled)
{
    // Export is off unless a collector is running, so a bare `make run` does not fill the
    // log with failed exports. Sampling defaults to 1.0: at a lower rate most conversations
    // produce no trace at all, which reads as "tracing is broken" rather than "sampled".
    builder.Services.AddOpenTelemetry()
        .ConfigureResource(r => r.AddService(Tracing.ServiceName))
        .WithTracing(t => t
            .AddSource(Tracing.ServiceName)
            .AddAspNetCoreInstrumentation()
            .SetSampler(new ParentBasedSampler(new TraceIdRatioBasedSampler(cfg.Obs.TraceSampling)))
            .AddOtlpExporter(o =>
            {
                o.Endpoint = new Uri(cfg.Obs.OtlpEndpoint.TrimEnd('/') + "/v1/traces");
                o.Protocol = OtlpExportProtocol.HttpProtobuf;
            }));
}

var metrics = new CustomerService.Obs.Metrics();
DotNetStats.Register(metrics.Registry);
builder.Services.AddSingleton(metrics);

if (cfg.Admin.Enabled && cfg.Admin.CorsOrigins.Count > 0)
{
    // Only for an admin UI served from another origin. The Compose stack proxies the UI's
    // /api through nginx, so nothing is cross-origin there and this stays off.
    builder.Services.AddCors(o => o.AddDefaultPolicy(p => p
        .WithOrigins(cfg.Admin.CorsOrigins.ToArray()).AllowAnyHeader().AllowAnyMethod()));
}

var app = builder.Build();
var log = app.Logger;

using var startup = new CancellationTokenSource(TimeSpan.FromMinutes(2));
var db = await Database.OpenAsync(cfg.Postgres.ConnectionString(), cfg.Rag.Dimensions, startup.Token);
var onnx = new OnnxEmbedder(new OnnxOptions(cfg.Rag.ModelPath, cfg.Rag.TokenizerPath, cfg.Rag.Dimensions, cfg.Rag.QueryPrefix, cfg.Rag.PassagePrefix,
    cfg.Rag.IntraOpThreads > 0 ? cfg.Rag.IntraOpThreads : null));
// Bounded on measurement, not on principle: see Rag/BoundedEmbedder.cs.
IEmbedder embedder = new BoundedEmbedder(onnx, cfg.Rag.MaxConcurrentEmbeddings);
var vectors = new VectorStore(db);
if (cfg.Rag.IngestOnStartup)
    await Ingest.RunAsync(cfg.Rag.CorpusPath, embedder, vectors, log, startup.Token);

var model = ChatModels.Create(cfg.Chat);
var tickets = new TicketStore(db);
var recorder = new PostgresTurnRecorder(db);
// Turns still "running" from a process that died are marked interrupted, never invented.
var recovered = await recorder.RecoverAsync(TimeSpan.FromMinutes(30), startup.Token);
if (recovered > 0) log.LogWarning("marked {Count} turn records left running by an earlier process as interrupted", recovered);

var service = new ChatService(
    new ConversationMemory(db, cfg.Chat.MaxHistoryMessages),
    new Retriever(embedder, vectors, cfg.Rag.TopK, cfg.Rag.SimilarityThreshold, app.Services.GetRequiredService<ILogger<Retriever>>()),
    model,
    new ConversationBudget(cfg.Cost.ConversationTokenBudget, cfg.Cost.TrackedConversations),
    metrics,
    recorder,
    cfg.Chat.MaxTokens,
    app.Services.GetRequiredService<ILogger<ChatService>>(),
    new OrderLookup(),
    new SupportTickets(tickets, app.Services.GetRequiredService<ILogger<SupportTickets>>()));

app.MapChatEndpoints(service, cfg.Chat, log);

if (cfg.Admin.Enabled)
{
    var accounts = new StaffAccounts(db);
    if (cfg.Admin.SeedUsername is { } seedUser && cfg.Admin.SeedPassword is { } seedPassword)
    {
        if (await accounts.SeedAsync(seedUser, seedPassword, startup.Token))
            log.LogInformation("seeded the first staff account {Username} as admin", seedUser);
    }
    var feedback = new Feedback(db);
    var admin = new AdminServices(accounts, new StaffSessions(db, accounts, cfg.Admin.SessionTimeout), new AdminAudit(db),
        tickets, new Conversations(db, tickets, feedback), feedback);
    if (cfg.Admin.CorsOrigins.Count > 0) app.UseCors();
    app.MapAdminEndpoints(admin, cfg.Admin, log);
    log.LogInformation("operations admin API enabled at {Prefix}", AdminEndpoints.Prefix);
}
app.MapMetrics("/metrics", metrics.Registry);
app.MapDemoPage();
app.MapGet("/healthz", () => Results.Text("{\"status\":\"UP\"}\n", "application/json"));
app.MapGet("/readyz", async (CancellationToken ct) =>
{
    using var ping = CancellationTokenSource.CreateLinkedTokenSource(ct);
    ping.CancelAfter(TimeSpan.FromSeconds(2));
    try
    {
        await using var conn = await db.OpenConnectionAsync(ping.Token);
        return Results.Text("{\"status\":\"UP\"}\n", "application/json");
    }
    catch (Exception)
    {
        return Results.Text("{\"status\":\"DOWN\",\"detail\":\"database\"}\n", "application/json", statusCode: 503);
    }
});

// ProcessorCount is what the embedding concurrency bound defaults to, and .NET derives it
// from the cgroup CPU limit: logged so a pod says what it thinks it has.
app.Lifetime.ApplicationStarted.Register(() =>
    log.LogInformation("listening on {Addr}, provider {Provider}, model {Model}, processors {Processors}, embedding concurrency {EmbeddingConcurrency}",
        cfg.HttpAddr, model.Provider, model.Model, Environment.ProcessorCount,
        cfg.Rag.MaxConcurrentEmbeddings <= 0 ? Environment.ProcessorCount : cfg.Rag.MaxConcurrentEmbeddings));
app.Lifetime.ApplicationStopping.Register(() =>
    log.LogInformation("shutting down with a grace period of {Grace}", cfg.ShutdownTimeout));

try
{
    await app.RunAsync();
}
finally
{
    onnx.Dispose();
    await db.DisposeAsync();
}
return 0;

static (IPAddress, int) ParseAddr(string addr)
{
    // Go-style ":8082" or "127.0.0.1:8082".
    var idx = addr.LastIndexOf(':');
    var host = idx < 0 ? "" : addr[..idx];
    var port = int.Parse(idx < 0 ? addr : addr[(idx + 1)..]);
    return (host.Length == 0 ? IPAddress.Any : IPAddress.Parse(host), port);
}

static async Task<int> Healthcheck()
{
    var addr = Environment.GetEnvironmentVariable("HTTP_ADDR") is { Length: > 0 } a ? a : ":8082";
    var port = addr[(addr.LastIndexOf(':') + 1)..];
    using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
    try
    {
        var resp = await client.GetAsync($"http://127.0.0.1:{port}/readyz");
        if (resp.IsSuccessStatusCode) return 0;
        Console.Error.WriteLine($"readyz returned {(int)resp.StatusCode}");
        return 1;
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine(ex.Message);
        return 1;
    }
}
