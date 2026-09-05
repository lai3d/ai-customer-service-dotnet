using CustomerService.Store;
using Npgsql;
using Testcontainers.PostgreSql;

namespace CustomerService.Tests.Support;

/// <summary>
/// A real pgvector for the whole collection. Retrieval is measured against a real database
/// and the real embedding model, never a stub: a stubbed vector store would make these tests
/// fast and meaningless, because the thing asserted is that this corpus, embedded by this
/// model, ranks the right passage first through this SQL. None of it needs an API key.
///
/// TEST_POSTGRES_URL, when set, points at a database already running and skips Testcontainers.
/// </summary>
public abstract class PostgresFixture(int dimensions) : IAsyncLifetime
{
    PostgreSqlContainer? container;
    public NpgsqlDataSource Db { get; private set; } = null!;
    public string ConnectionString { get; private set; } = "";
    public int Dimensions => dimensions;

    public async ValueTask InitializeAsync()
    {
        var url = Environment.GetEnvironmentVariable("TEST_POSTGRES_URL");
        if (string.IsNullOrEmpty(url))
        {
            container = new PostgreSqlBuilder("pgvector/pgvector:pg17")
                .WithDatabase("csagent").WithUsername("csagent").WithPassword("csagent")
                .Build();
            await container.StartAsync();
            url = container.GetConnectionString();
        }
        // One container, one database per dimensionality: the schema pins vector(N).
        var admin = new NpgsqlConnectionStringBuilder(url);
        var dbName = $"csagent_{dimensions}_{Guid.NewGuid():N}"[..40];
        await using (var conn = new NpgsqlConnection(admin.ConnectionString))
        {
            await conn.OpenAsync();
            await using var cmd = new NpgsqlCommand($"CREATE DATABASE \"{dbName}\"", conn);
            await cmd.ExecuteNonQueryAsync();
        }
        admin.Database = dbName;
        // The production pool bound, so a test that opens a thousand turns meets the same
        // limit the service does. Without it Npgsql's default of 100 per data source met the
        // container's max_connections of 100 head-on: the benchmark's first runs failed a
        // handful of requests with "53300: sorry, too many clients already".
        admin.MaxPoolSize = 20;
        ConnectionString = admin.ConnectionString;
        Db = await Database.OpenAsync(ConnectionString, dimensions, CancellationToken.None);
    }

    public async ValueTask DisposeAsync()
    {
        if (Db is not null) await Db.DisposeAsync();
        if (container is not null) await container.DisposeAsync();
    }
}

/// <summary>Eight dimensions: for tests about the turn, where the vectors are a stub's.</summary>
public sealed class Postgres8 : PostgresFixture { public Postgres8() : base(8) { } }

/// <summary>The model's 384 dimensions: for retrieval measurements against the real embedder.</summary>
public sealed class Postgres384 : PostgresFixture { public Postgres384() : base(384) { } }

[CollectionDefinition("postgres-8")]
public sealed class Postgres8Collection : ICollectionFixture<Postgres8>;

[CollectionDefinition("postgres-384")]
public sealed class Postgres384Collection : ICollectionFixture<Postgres384>;
