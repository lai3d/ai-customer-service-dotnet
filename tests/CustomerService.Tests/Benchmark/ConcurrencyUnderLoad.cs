using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using CustomerService.Admin;
using CustomerService.Chat;
using CustomerService.Config;
using CustomerService.Cost;
using CustomerService.HttpApi;
using CustomerService.Llm;
using CustomerService.Obs;
using CustomerService.Rag;
using CustomerService.Tests.Support;
using CustomerService.Tickets;
using CustomerService.Tools;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CustomerService.Tests.Benchmark;

/// <summary>
/// What the runtime does with a thousand in-flight requests. Skipped unless BENCH is set: it
/// measures a machine rather than asserting a behaviour. Run it with `make bench`, which
/// invokes one process per variant.
///
/// The parameters match the Java and Go implementations' exactly, so the numbers can be put
/// side by side: 1000 concurrent requests, a stubbed model with a fixed 1000 ms delay, the
/// full production request path -- validation, conversation memory in Postgres, the turn
/// record, query embedding, a pgvector search, tool definitions, metrics -- over a real
/// Kestrel socket, one fresh conversation per request.
/// </summary>
[Collection("postgres-384")]
public class ConcurrencyUnderLoad(Postgres384 pg)
{
    const int Concurrency = 1000;
    static readonly TimeSpan ModelDelay = TimeSpan.FromSeconds(1);

    /// <summary>An LLM call is mostly waiting; a real one would add cost, network variance and rate limits to a measurement about scheduling.</summary>
    sealed class SlowModel(Func<TimeSpan> delay) : IChatModel
    {
        public string Provider => "stub"; public string Model => "stub-model";
        public async Task<ModelResult> StreamAsync(ModelRequest request, Func<string, ValueTask> onText, CancellationToken ct)
        {
            await Task.Delay(delay(), ct);
            await onText("ok");
            return new ModelResult("ok", [], "end_turn", new Usage(1000, 10), "stub-model");
        }
    }

    static readonly SlowModel Fixed = new(() => ModelDelay);

    // 300 ms plus an exponential with a 700 ms mean: the same 1000 ms mean, a median near
    // 785 ms, and a tail that reaches several seconds, capped at 8 s. A fixed delay flatters
    // every runtime because nothing ever queues behind something slow.
    static readonly SlowModel Varying = new(() =>
    {
        var u = Random.Shared.NextDouble();
        var ms = 300 + -Math.Log(1 - u) * 700;
        return TimeSpan.FromMilliseconds(Math.Min(ms, 8000));
    });

    sealed record Result(string Name, string DelayNote, TimeSpan Wall, double RequestsPerSec, TimeSpan P50, TimeSpan P95, TimeSpan P99,
        int OsThreadsBefore, int OsThreadsPeak, int PoolThreadsBefore, int PoolThreadsPeak, long PoolQueuePeak, int Failures, string FailureKinds);

    [Fact]
    public async Task AThousandConcurrentRequests()
    {
        Assert.SkipUnless(Environment.GetEnvironmentVariable("BENCH") is { Length: > 0 }, "set BENCH=1 to run the benchmark; it measures a machine, not a behaviour");
        Assert.SkipUnless(Repo.ModelPresent, "embedding model not present; run scripts/fetch-deps.sh");
        var variant = Environment.GetEnvironmentVariable("BENCH_EMBEDDER") ?? "onnx";
        var output = TestContext.Current.TestOutputHelper!;

        int? intra = int.TryParse(Environment.GetEnvironmentVariable("BENCH_ORT_INTRA"), out var n) ? n : null;
        var intraNote = intra is null ? "" : $", intra-op threads {intra}";
        OnnxEmbedder Onnx() => new(new OnnxOptions(Repo.ModelPath, Repo.TokenizerPath, 384, "query: ", "passage: ", intra));
        var r = variant switch
        {
            "stub" => await Run("stubbed embedding", () => new StubEmbedder(384), Fixed, null),
            "bounded" => await Run($"ONNX, bounded to {Environment.ProcessorCount}{intraNote}", () => new BoundedEmbedder(Onnx(), 0), Fixed, null),
            "varying" => await Run("ONNX, varying model delay" + intraNote, Onnx, Varying, "300ms + Exp(mean 700ms) model delay, capped at 8s"),
            _ => await Run("in-process ONNX embedding" + intraNote, Onnx, Fixed, null),
        };

        output.WriteLine($"{Concurrency} concurrent requests, {r.DelayNote}, ProcessorCount={Environment.ProcessorCount}, " +
            $"pool min threads={MinThreads()}, {RuntimeInformation()}");
        output.WriteLine($"{"run",-28} {"wall",8} {"req/s",6} {"p50",8} {"p95",8} {"p99",8} {"OS threads",12} {"pool threads",13} {"pool queue",10} {"failed",6}");
        output.WriteLine($"{r.Name,-28} {(int)r.Wall.TotalMilliseconds,6}ms {r.RequestsPerSec,6:F0} {(int)r.P50.TotalMilliseconds,6}ms {(int)r.P95.TotalMilliseconds,6}ms {(int)r.P99.TotalMilliseconds,6}ms " +
            $"{r.OsThreadsBefore,5}->{r.OsThreadsPeak,-5} {r.PoolThreadsBefore,5}->{r.PoolThreadsPeak,-6} {r.PoolQueuePeak,10} {r.Failures,6}");
        if (variant == "varying")
            output.WriteLine("wall and req/s are not throughput here: with a heavy tail the wall time is the slowest single request. p50 and p95 are the numbers that mean something.");
        if (r.Failures > 0) output.WriteLine($"failures: {r.FailureKinds}");
        Assert.True(r.Failures == 0, $"{r.Failures} of {Concurrency} requests failed ({r.FailureKinds}); the numbers above are not a measurement of a working service");
    }

    static int MinThreads() { ThreadPool.GetMinThreads(out var w, out _); return w; }
    static string RuntimeInformation() => $"{System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription} on {System.Runtime.InteropServices.RuntimeInformation.OSDescription} {System.Runtime.InteropServices.RuntimeInformation.OSArchitecture}";

    async Task<Result> Run(string name, Func<IEmbedder> newEmbedder, IChatModel model, string? delayNote)
    {
        using var embedder = newEmbedder();
        var store = new VectorStore(pg.Db);
        await Ingest.RunAsync(Repo.CorpusPath, embedder, store, NullLogger.Instance, CancellationToken.None);
        await using (var clear = pg.Db.CreateCommand("TRUNCATE chat_memory, conversation_turn, turn_model_call, answer_feedback"))
            await clear.ExecuteNonQueryAsync();

        // The production path, wired the way Program.cs wires it, with the model stubbed.
        var tickets = new TicketStore(pg.Db);
        var service = new ChatService(new ConversationMemory(pg.Db, 40), new Retriever(embedder, store, 8, 0), model,
            new ConversationBudget(0, 20_000), new Metrics(), new PostgresTurnRecorder(pg.Db), 1024, null,
            new OrderLookup(), new SupportTickets(tickets));

        // Server-side failures are the other half of a failed request: capture what the turn
        // logged, so a 500 in the table has a cause next to it.
        var errors = new System.Collections.Concurrent.ConcurrentDictionary<string, int>();
        var capture = new CapturingLoggerProvider(errors);
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.Logging.AddProvider(capture);
        builder.Logging.SetMinimumLevel(LogLevel.Warning);
        builder.WebHost.ConfigureKestrel(k => { k.Listen(IPAddress.Loopback, 0); k.Limits.MaxConcurrentConnections = null; });
        var app = builder.Build();
        app.MapChatEndpoints(service, new ChatConfig("stub", "stub", "k", "", 1024, 40, 64, 4000, 1, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(120), TimeSpan.FromSeconds(1)), app.Logger);
        await app.StartAsync();
        var url = app.Urls.First();

        // Let the process settle before taking the baseline: ingestion has just run a batched
        // forward pass through ONNX Runtime.
        GC.Collect();
        await Task.Delay(500);
        int osBefore = OsThreads(), poolBefore = ThreadPool.ThreadCount;
        int osPeak = osBefore, poolPeak = poolBefore; long queuePeak = 0;

        // Whole-process counts include the load driver, so these are an upper bound on what
        // serving costs; the stubbed-embedding variant is the control that isolates the
        // embedding model's share. The sampler is a dedicated thread, not a pool work item, so
        // a starved pool cannot stop it from sampling the starvation.
        using var stopSampling = new CancellationTokenSource();
        var sampler = new Thread(() =>
        {
            while (!stopSampling.IsCancellationRequested)
            {
                osPeak = Math.Max(osPeak, OsThreads());
                poolPeak = Math.Max(poolPeak, ThreadPool.ThreadCount);
                queuePeak = Math.Max(queuePeak, ThreadPool.PendingWorkItemCount);
                Thread.Sleep(5);
            }
        }) { IsBackground = true, Name = "bench-sampler" };
        sampler.Start();

        using var client = new HttpClient(new SocketsHttpHandler { MaxConnectionsPerServer = Concurrency, PooledConnectionLifetime = TimeSpan.FromMinutes(5) })
        { Timeout = TimeSpan.FromSeconds(120) };
        var latencies = new long[Concurrency];
        var failures = new int[Concurrency];
        var kinds = new System.Collections.Concurrent.ConcurrentDictionary<string, int>();
        var gate = new TaskCompletionSource();
        var workers = Enumerable.Range(0, Concurrency).Select(async i =>
        {
            await gate.Task;
            var sw = Stopwatch.StartNew();
            try
            {
                // A fresh conversation per request: no shared history, no lock contention that
                // belongs to the test rather than the service.
                using var res = await client.PostAsJsonAsync(url + "/api/v1/chat", new { conversationId = $"bench-{i}", message = "how much is delivery?" });
                var body = await res.Content.ReadAsStringAsync();
                if (res.StatusCode != HttpStatusCode.OK) { failures[i] = 1; kinds.AddOrUpdate($"HTTP {(int)res.StatusCode} {body[..Math.Min(80, body.Length)]}", 1, (_, n) => n + 1); }
            }
            catch (Exception ex) { failures[i] = 1; kinds.AddOrUpdate(ex.GetType().Name + ": " + ex.Message[..Math.Min(80, ex.Message.Length)], 1, (_, n) => n + 1); }
            latencies[i] = sw.ElapsedMilliseconds;
        }).ToArray();

        var started = Stopwatch.StartNew();
        gate.SetResult();
        await Task.WhenAll(workers);
        var wall = started.Elapsed;
        stopSampling.Cancel();
        sampler.Join();
        await app.StopAsync();
        await app.DisposeAsync();

        Array.Sort(latencies);
        return new Result(name, delayNote ?? $"{(int)ModelDelay.TotalMilliseconds}ms fixed stubbed model delay", wall, Concurrency / wall.TotalSeconds,
            TimeSpan.FromMilliseconds(latencies[Concurrency * 50 / 100]), TimeSpan.FromMilliseconds(latencies[Concurrency * 95 / 100]), TimeSpan.FromMilliseconds(latencies[Concurrency * 99 / 100]),
            osBefore, osPeak, poolBefore, poolPeak, queuePeak, failures.Sum(),
            string.Join(" | ", kinds.Select(k => $"{k.Value}x {k.Key}").Concat(errors.Select(e => $"server logged {e.Value}x {e.Key}"))));
    }

    /// <summary>Current OS threads of this process. Unlike Go's threadcreate profile it goes down as well as up, so the sampler tracks the peak.</summary>
    static int OsThreads()
    {
        // /proc is exact and cheap on Linux; Process.Threads allocates a snapshot elsewhere.
        if (OperatingSystem.IsLinux() && File.Exists("/proc/self/status"))
        {
            foreach (var line in File.ReadLines("/proc/self/status"))
                if (line.StartsWith("Threads:")) return int.Parse(line.AsSpan(8).Trim());
        }
        return Process.GetCurrentProcess().Threads.Count;
    }

    /// <summary>Keeps the distinct warning-and-above messages the server logged, with their exception types.</summary>
    sealed class CapturingLoggerProvider(System.Collections.Concurrent.ConcurrentDictionary<string, int> sink) : ILoggerProvider
    {
        public ILogger CreateLogger(string categoryName) => new Sink(sink);
        public void Dispose() { }
        sealed class Sink(System.Collections.Concurrent.ConcurrentDictionary<string, int> sink) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel level) => level >= LogLevel.Warning;
            public void Log<TState>(LogLevel level, EventId id, TState state, Exception? ex, Func<TState, Exception?, string> fmt)
            {
                var key = fmt(state, ex) + (ex is null ? "" : $" :: {ex.GetType().Name}: {ex.Message[..Math.Min(140, ex.Message.Length)]}");
                sink.AddOrUpdate(key, 1, (_, n) => n + 1);
            }
        }
    }
}
