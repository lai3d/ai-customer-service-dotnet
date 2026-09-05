// Every tunable comes from the environment, in one place, with the reasoning for each
// default written next to it. Several of the numbers are measurements rather than taste,
// and the comments say which.
namespace CustomerService.Config;

public sealed record AppConfig(
    string HttpAddr,
    TimeSpan ShutdownTimeout,
    PostgresConfig Postgres,
    ChatConfig Chat,
    RagConfig Rag,
    CostConfig Cost,
    ObsConfig Obs,
    AdminConfig Admin)
{
    /// <summary>Loads configuration from the process environment.</summary>
    public static AppConfig Load() => Load(Environment.GetEnvironmentVariable);

    /// <summary>
    /// Loads configuration from an environment lookup. A missing API key for the selected
    /// provider fails here rather than on the first customer request: a service that
    /// starts, reports itself healthy, is marked ready by Kubernetes and then 401s every
    /// customer is the worse failure.
    /// </summary>
    public static AppConfig Load(Func<string, string?> env)
    {
        var e = new Env(env);
        var provider = e.Str("CHAT_PROVIDER", "anthropic").ToLowerInvariant();
        var (model, apiKey, baseUrl, keyVar) = ResolveProvider(provider, e);
        if (string.IsNullOrEmpty(apiKey))
            throw new ConfigException($"chat provider \"{provider}\" selected but {keyVar} is not set");

        return new AppConfig(
            // 8082, not 8080 or 8081: the Java and Go implementations of this system use
            // those, and all three stacks are expected to run on one machine.
            HttpAddr: e.Str("HTTP_ADDR", ":8082"),
            ShutdownTimeout: e.Duration("SHUTDOWN_GRACE", TimeSpan.FromSeconds(30)),
            Postgres: new PostgresConfig(
                Host: e.Str("POSTGRES_HOST", "localhost"),
                Port: e.Int("POSTGRES_PORT", 5432),
                Database: e.Str("POSTGRES_DB", "csagent"),
                User: e.Str("POSTGRES_USER", "csagent"),
                Password: e.Str("POSTGRES_PASSWORD", "csagent"),
                MaxConns: e.Int("POSTGRES_MAX_CONNS", 20)),
            Chat: new ChatConfig(
                Provider: provider,
                Model: model,
                ApiKey: apiKey,
                BaseUrl: baseUrl,
                MaxTokens: e.Int("CHAT_MAX_TOKENS", 8192),
                MaxHistoryMessages: e.Int("CHAT_MAX_HISTORY_MESSAGES", 40),
                MaxConversationIdLength: 64,
                MaxMessageLength: e.Int("CHAT_MAX_MESSAGE_LENGTH", 4000),
                RetryMaxAttempts: e.Int("AI_RETRY_MAX_ATTEMPTS", 3),
                ConnectTimeout: e.Duration("HTTP_CONNECT_TIMEOUT", TimeSpan.FromSeconds(10)),
                RequestTimeout: e.Duration("HTTP_READ_TIMEOUT", TimeSpan.FromSeconds(120)),
                KeepAliveInterval: e.Duration("SSE_KEEPALIVE", TimeSpan.FromSeconds(15))),
            Rag: new RagConfig(
                CorpusPath: e.Str("FAQ_CORPUS_PATH", "corpus/faq.json"),
                IngestOnStartup: e.Bool("FAQ_INGEST_ON_STARTUP", true),
                ModelPath: e.Str("EMBEDDING_MODEL_PATH", "model-cache/multilingual-e5-small/model.onnx"),
                TokenizerPath: e.Str("EMBEDDING_TOKENIZER_PATH", "model-cache/multilingual-e5-small/tokenizer.json"),
                Dimensions: e.Int("EMBEDDING_DIMENSIONS", 384),
                TopK: e.Int("RAG_TOP_K", 8),
                SimilarityThreshold: e.Double("RAG_SIMILARITY_THRESHOLD", 0),
                QueryPrefix: e.Str("EMBEDDING_QUERY_PREFIX", "query: "),
                PassagePrefix: e.Str("EMBEDDING_PASSAGE_PREFIX", "passage: "),
                MaxConcurrentEmbeddings: e.Int("EMBEDDING_MAX_CONCURRENCY", 0),
                IntraOpThreads: e.Int("EMBEDDING_INTRA_OP_THREADS", 1)),
            Cost: new CostConfig(
                ConversationTokenBudget: e.Int("CONVERSATION_TOKEN_BUDGET", 200_000),
                TrackedConversations: e.Int("TRACKED_CONVERSATIONS", 10_000)),
            Obs: new ObsConfig(
                OtlpEndpoint: e.Str("OTLP_TRACING_ENDPOINT", "http://localhost:4318"),
                OtlpEnabled: e.Bool("OTLP_TRACING_EXPORT_ENABLED", false),
                TraceSampling: e.Double("TRACING_SAMPLE_RATE", 1.0),
                IncludeQueryContent: e.Bool("TRACE_INCLUDE_QUERY_CONTENT", false)),
            Admin: AdminConfig.From(e.Bool("ADMIN_ENABLED", false), e.Raw("ADMIN_SEED_USERNAME"), e.Raw("ADMIN_SEED_PASSWORD"),
                e.Duration("ADMIN_SESSION_TIMEOUT", TimeSpan.FromMinutes(30)), e.Str("ADMIN_CORS_ORIGINS", "")));
    }

    static (string model, string? apiKey, string baseUrl, string keyVar) ResolveProvider(string provider, Env e) =>
        provider switch
        {
            // Sampling parameters are deliberately absent everywhere: Claude Opus 5 returns
            // HTTP 400 for temperature, top_p or top_k, and GPT-5 accepts only its own
            // default. There is no field in this codebase to set one.
            "anthropic" => (e.Str("ANTHROPIC_CHAT_MODEL", "claude-opus-5"), e.Raw("ANTHROPIC_API_KEY"),
                            e.Str("ANTHROPIC_BASE_URL", ""), "ANTHROPIC_API_KEY"),
            "openai" => (e.Str("OPENAI_CHAT_MODEL", "gpt-5"), e.Raw("OPENAI_API_KEY"),
                         e.Str("OPENAI_BASE_URL", "https://api.openai.com/v1"), "OPENAI_API_KEY"),
            // A separate provider reached over a shared protocol. Putting an xAI key in
            // OPENAI_API_KEY with a base-URL override works and lies: the configuration
            // then says OpenAI everywhere while talking to xAI.
            "xai" => (e.Str("XAI_CHAT_MODEL", "grok-4.6"), e.Raw("XAI_API_KEY"),
                      e.Str("XAI_BASE_URL", "https://api.x.ai/v1"), "XAI_API_KEY"),
            // Gemini is not here on purpose. A provider that configuration accepts and the
            // client layer cannot build would fail later and less clearly than this does.
            _ => throw new ConfigException($"unknown CHAT_PROVIDER \"{provider}\": want anthropic, openai or xai"),
        };

    sealed class Env(Func<string, string?> lookup)
    {
        public string? Raw(string key) => lookup(key);
        public string Str(string key, string fallback) => lookup(key) is { Length: > 0 } v ? v : fallback;
        public int Int(string key, int fallback) => int.TryParse(lookup(key), out var n) ? n : fallback;
        public double Double(string key, double fallback) =>
            double.TryParse(lookup(key), System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var d) ? d : fallback;
        public bool Bool(string key, bool fallback) => bool.TryParse(lookup(key), out var b) ? b : fallback;
        // Accepts Go-style durations ("30s", "2m", "500ms") as well as .NET TimeSpan text,
        // so a .env shared with the sibling implementations means the same thing here.
        public TimeSpan Duration(string key, TimeSpan fallback) =>
            lookup(key) is { Length: > 0 } v && Durations.TryParse(v, out var d) ? d : fallback;
    }
}

public sealed class ConfigException(string message) : Exception(message);

public sealed record PostgresConfig(string Host, int Port, string Database, string User, string Password, int MaxConns)
{
    /// <summary>
    /// The Npgsql connection string. Built from keywords rather than by formatting a URL,
    /// so a password containing / ? # @ or : -- all legal and common in generated ones --
    /// cannot turn into a different URL or one that will not parse.
    /// </summary>
    public string ConnectionString()
    {
        var b = new Npgsql.NpgsqlConnectionStringBuilder
        {
            Host = Host, Port = Port, Database = Database, Username = User, Password = Password,
            SslMode = Npgsql.SslMode.Disable,
            // A pool this size is a bound on how much concurrency reaches Postgres rather
            // than a throughput setting; raising it bought about 7% in the Java
            // implementation's benchmark, so it is not where the time goes.
            MaxPoolSize = MaxConns,
        };
        return b.ConnectionString;
    }
}

public sealed record ChatConfig(
    string Provider,
    string Model,
    string ApiKey,
    string BaseUrl,
    int MaxTokens,
    // How many messages of history travel with each request. Every one is re-sent and
    // re-billed on every turn, so this is a cost lever, not just a memory setting.
    int MaxHistoryMessages,
    // A conversation id is stored in a bounded column and echoed in a header; an
    // unvalidated, unbounded id from a client turned into a 500 in the Java version.
    int MaxConversationIdLength,
    int MaxMessageLength,
    // Interactive retry. Library defaults are chosen for batch jobs: Spring AI's were 10
    // attempts with a 180s cap, 1142 seconds of backoff before the customer is told it
    // failed. Three attempts caps the added wait at a few seconds.
    int RetryMaxAttempts,
    // Guards against a stall, not against slowness: a long answer legitimately takes time.
    // The SDKs' default request timeout is ten minutes, which is a stall for an
    // interactive turn.
    TimeSpan ConnectTimeout,
    TimeSpan RequestTimeout,
    // SSE connections are legitimately idle between the request and the first token, and
    // proxies close idle connections.
    TimeSpan KeepAliveInterval);

public sealed record RagConfig(
    string CorpusPath,
    bool IngestOnStartup,
    string ModelPath,
    string TokenizerPath,
    int Dimensions,
    // Passages per question, inherited from the Java implementation's measurement.
    int TopK,
    // Zero, and that is a measurement rather than an omission. With multilingual-e5-small
    // the scores of relevant questions, off-topic questions and degenerate input all
    // overlap: the weakest real match scores 0.8378, the strongest off-topic question
    // 0.8490, and three Chinese full stops 0.8417. No value filters relevance, and none
    // works as a floor for junk either. Relevance judgement lives in the system prompt.
    // NoSimilarityThresholdIsUseful holds the measurement; re-measure before setting it.
    double SimilarityThreshold,
    // e5 is trained with asymmetric input markers. They are part of the model contract;
    // applying them to one side only is worse than applying neither.
    string QueryPrefix,
    string PassagePrefix,
    // How many callers may be inside the native embedding call at once. 0 means the
    // processor count. The work is CPU-bound, so admitting more than there are cores
    // buys thread-pool threads and nothing else -- and a thread blocked in native code is
    // one the pool's hill-climbing has to notice and replace.
    int MaxConcurrentEmbeddings,
    // Threads ONNX Runtime may use inside one forward pass. 1, and that is a measurement:
    // the runtime's default is the core count, and under concurrent queries every caller's
    // pass brings its own core-count of threads -- eighteen concurrent queries contending
    // on eighteen cores with three hundred threads. With 1 the benchmark's p50 fell from
    // 3451 ms to 1843 ms. 0 restores the runtime's default. See docs/benchmark.md.
    int IntraOpThreads);

public sealed record CostConfig(
    // A conversation is an open-ended bill unless capped: a message window bounds any
    // single request, nothing bounds the number of requests. 0 disables the cap.
    long ConversationTokenBudget,
    int TrackedConversations);

public sealed record ObsConfig(
    string OtlpEndpoint,
    bool OtlpEnabled,
    double TraceSampling,
    // Spring AI attached the customer's query to every vector-store span with no property
    // to disable it. Nothing here does that by default; this switch exists so the choice
    // is deliberate rather than accidental.
    bool IncludeQueryContent);

/// <summary>
/// The operations surface. Off unless configured: with ADMIN_ENABLED unset the admin routes
/// are never registered, and /api/admin/v1/* is a 404 the way any unknown path is -- not a
/// 401 from a guard. A guard is a thing that can be misconfigured; an absent route cannot be.
/// </summary>
public sealed record AdminConfig(
    bool Enabled,
    // Create the first admin at startup, only into an empty staff_account table. Never
    // overwrites or resets an account; safe to leave set. One without the other refuses to start.
    string? SeedUsername,
    string? SeedPassword,
    // Idle timeout of a staff session. There is no absolute lifetime and no concurrent-session limit.
    TimeSpan SessionTimeout,
    // Origins the separately deployed admin UI is served from, for CORS. Empty when the UI
    // is proxied through the same origin, which is what the Compose stack does.
    IReadOnlyList<string> CorsOrigins)
{
    public static AdminConfig From(bool enabled, string? seedUser, string? seedPassword, TimeSpan timeout, string origins)
    {
        if (string.IsNullOrEmpty(seedUser) != string.IsNullOrEmpty(seedPassword))
            throw new ConfigException("ADMIN_SEED_USERNAME and ADMIN_SEED_PASSWORD must be set together");
        if (!string.IsNullOrEmpty(seedPassword) && seedPassword.Length < 12)
            throw new ConfigException("ADMIN_SEED_PASSWORD must be at least 12 characters: it is the credential for every customer conversation in the database");
        return new AdminConfig(enabled, string.IsNullOrEmpty(seedUser) ? null : seedUser, string.IsNullOrEmpty(seedPassword) ? null : seedPassword,
            timeout, origins.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
    }
}

public static class Durations
{
    /// <summary>Parses "10s", "2m", "500ms", "1h30m" or a .NET TimeSpan string.</summary>
    public static bool TryParse(string text, out TimeSpan value)
    {
        value = TimeSpan.Zero;
        text = text.Trim();
        if (text.Length == 0) return false;
        if (TimeSpan.TryParse(text, System.Globalization.CultureInfo.InvariantCulture, out value) && text.Contains(':'))
            return true;
        var total = TimeSpan.Zero;
        int i = 0;
        while (i < text.Length)
        {
            int start = i;
            while (i < text.Length && (char.IsDigit(text[i]) || text[i] == '.')) i++;
            if (start == i) return false;
            if (!double.TryParse(text.AsSpan(start, i - start), System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var n)) return false;
            int unitStart = i;
            while (i < text.Length && char.IsLetter(text[i])) i++;
            var unit = text[unitStart..i];
            total += unit switch
            {
                "ms" => TimeSpan.FromMilliseconds(n),
                "s" => TimeSpan.FromSeconds(n),
                "m" => TimeSpan.FromMinutes(n),
                "h" => TimeSpan.FromHours(n),
                _ => TimeSpan.MinValue,
            };
            if (total == TimeSpan.MinValue) return false;
        }
        value = total;
        return true;
    }
}
