using System.Reflection;
using CustomerService.Config;
using CustomerService.Llm;

namespace CustomerService.Tests;

public class ConfigTests
{
    static Func<string, string?> Env(params (string, string)[] pairs)
    {
        var d = pairs.ToDictionary(p => p.Item1, p => p.Item2);
        return k => d.TryGetValue(k, out var v) ? v : null;
    }

    [Fact]
    public void StartupFailsWhenTheSelectedProviderHasNoKey()
    {
        var ex = Assert.Throws<ConfigException>(() => AppConfig.Load(Env(("CHAT_PROVIDER", "openai"), ("ANTHROPIC_API_KEY", "set-but-not-selected"))));
        Assert.Contains("OPENAI_API_KEY", ex.Message);
    }

    [Fact]
    public void OnlyTheSelectedProvidersKeyIsRequired()
    {
        var cfg = AppConfig.Load(Env(("ANTHROPIC_API_KEY", "k")));
        Assert.Equal("anthropic", cfg.Chat.Provider);
        Assert.Equal("claude-opus-5", cfg.Chat.Model);
        var xai = AppConfig.Load(Env(("CHAT_PROVIDER", "xai"), ("XAI_API_KEY", "k")));
        Assert.Equal("grok-4.6", xai.Chat.Model);
        Assert.Equal("https://api.x.ai/v1", xai.Chat.BaseUrl);
    }

    [Fact]
    public void AnUnknownProviderIsRejectedByName()
    {
        var ex = Assert.Throws<ConfigException>(() => AppConfig.Load(Env(("CHAT_PROVIDER", "gemini"), ("GEMINI_API_KEY", "k"))));
        Assert.Contains("gemini", ex.Message);
    }

    /// <summary>
    /// Sounds like testing nothing until you know the shape of the bug it prevents: Spring
    /// AI set a temperature in a field initialiser on each provider's properties class that
    /// configuration could not null out, and Claude Opus 5 returns HTTP 400 for it.
    /// </summary>
    [Fact]
    public void NoSamplingParameterIsConfigurable()
    {
        // The types that reach a model call. RagConfig.TopK is a retrieval setting, not a sampling one.
        foreach (var type in new[] { typeof(ChatConfig), typeof(ModelRequest), typeof(ModelOptions) })
            foreach (var p in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                var n = p.Name.ToLowerInvariant();
                Assert.False(n.Contains("temperature") || n.Contains("topp") || n.Contains("topk") || n.Contains("top_p") || n.Contains("top_k"),
                    $"{type.Name}.{p.Name} looks like a sampling parameter");
            }
    }

    [Fact]
    public void TheDefaultPortMatchesWhatTheDocumentsPromise()
    {
        var cfg = AppConfig.Load(Env(("ANTHROPIC_API_KEY", "k")));
        Assert.Equal(":8082", cfg.HttpAddr);
    }

    [Theory]
    [InlineData("30s", 30_000)]
    [InlineData("2m", 120_000)]
    [InlineData("500ms", 500)]
    [InlineData("1h30m", 5_400_000)]
    [InlineData("00:00:45", 45_000)]
    public void DurationsAcceptTheSiblingImplementationsSyntax(string text, int millis)
    {
        Assert.True(Durations.TryParse(text, out var d));
        Assert.Equal(millis, (int)d.TotalMilliseconds);
    }

    /// <summary>
    /// A password containing any of / ? # @ : -- all legal in a Postgres password and common
    /// in generated ones -- must reach the driver intact.
    /// </summary>
    [Fact]
    public void CredentialsWithUrlSyntaxSurviveIntoTheConnectionString()
    {
        var pg = new PostgresConfig("db.internal", 5432, "csagent", "svc/user", "test/a#b%?@:=;'", 20);
        var parsed = new Npgsql.NpgsqlConnectionStringBuilder(pg.ConnectionString());
        Assert.Equal("test/a#b%?@:=;'", parsed.Password);
        Assert.Equal("svc/user", parsed.Username);
        Assert.Equal("db.internal", parsed.Host);
    }
}
