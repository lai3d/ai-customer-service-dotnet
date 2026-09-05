using Npgsql;

namespace CustomerService.Store;

/// <summary>
/// Owns the Postgres data source and the schema. Conversation memory and the FAQ vectors
/// live in the same database on purpose: one database to run, back up and reason about, and
/// a ticket and the conversation that produced it can be written in one transaction if they
/// ever stop being mock data.
/// </summary>
public static class Database
{
    /// <summary>
    /// Applied at startup. Idempotent, and small enough that a migration tool would be more
    /// machinery than the problem needs. conversation_id is varchar(64) rather than
    /// unbounded: an id arrives from a client, and in the Java implementation an over-long
    /// one surfaced as a 500 from a database constraint. It is validated at the edge as
    /// well, so the column is a backstop.
    /// </summary>
    public const string Schema = """
        CREATE EXTENSION IF NOT EXISTS vector;

        CREATE TABLE IF NOT EXISTS chat_memory (
            id              BIGSERIAL PRIMARY KEY,
            conversation_id VARCHAR(64) NOT NULL,
            role            TEXT        NOT NULL CHECK (role IN ('user', 'assistant')),
            content         TEXT        NOT NULL,
            created_at      TIMESTAMPTZ NOT NULL DEFAULT now()
        );

        CREATE INDEX IF NOT EXISTS chat_memory_conversation_idx
            ON chat_memory (conversation_id, id);

        -- Readable primary keys: "faq:returns-window:en" rather than an opaque UUID, so a row
        -- can be traced back to its corpus entry by eye.
        CREATE TABLE IF NOT EXISTS faq_document (
            id         TEXT NOT NULL PRIMARY KEY,
            entry_id   TEXT NOT NULL,
            language   TEXT NOT NULL,
            category   TEXT NOT NULL,
            question   TEXT NOT NULL,
            answer     TEXT NOT NULL,
            content    TEXT NOT NULL,
            embedding  vector({0}) NOT NULL
        );

        CREATE INDEX IF NOT EXISTS faq_document_embedding_idx
            ON faq_document USING hnsw (embedding vector_cosine_ops);
        """;

    // An arbitrary constant shared by every replica of this service and by nothing else.
    // Postgres advisory locks live in one 64-bit keyspace for the whole database, so the
    // value matters only in that two different applications must not collide on it.
    public const long SchemaLockKey = 0x41_49_43_53_4E_45_54; // "AICSNET"

    /// <summary>
    /// Applies the schema on a single connection, then builds a data source whose
    /// connections know about the vector type. Registering the type looks up the OID
    /// <c>CREATE EXTENSION vector</c> creates, so the extension has to exist first.
    /// </summary>
    public static async Task<NpgsqlDataSource> OpenAsync(string connectionString, int dimensions, CancellationToken ct)
    {
        await ApplySchemaAsync(connectionString, dimensions, ct);
        var builder = new NpgsqlDataSourceBuilder(connectionString);
        builder.UseVector();
        var db = builder.Build();
        try
        {
            await using var conn = await db.OpenConnectionAsync(ct);
        }
        catch
        {
            await db.DisposeAsync();
            throw;
        }
        return db;
    }

    /// <summary>
    /// Serialises DDL across replicas. <c>CREATE EXTENSION IF NOT EXISTS</c> is not
    /// concurrency-safe: it checks the catalogue and then inserts, with nothing holding the
    /// gap, so two replicas starting together against a cold database crash one of them
    /// with a duplicate-key error on pg_extension_name_index. The advisory lock is released
    /// explicitly so the window is the DDL rather than the rest of startup.
    /// </summary>
    public static async Task ApplySchemaAsync(string connectionString, int dimensions, CancellationToken ct)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(ct);
        await using (var lockCmd = new NpgsqlCommand("SELECT pg_advisory_lock($1)", conn))
        {
            lockCmd.Parameters.Add(new NpgsqlParameter { Value = SchemaLockKey });
            await lockCmd.ExecuteNonQueryAsync(ct);
        }
        try
        {
            await using var ddl = new NpgsqlCommand(Schema.Replace("{0}", dimensions.ToString()), conn);
            await ddl.ExecuteNonQueryAsync(ct);
        }
        finally
        {
            // A failure to unlock is not worth failing startup for: closing the connection
            // releases it anyway.
            await using var unlock = new NpgsqlCommand("SELECT pg_advisory_unlock($1)", conn);
            unlock.Parameters.Add(new NpgsqlParameter { Value = SchemaLockKey });
            try { await unlock.ExecuteNonQueryAsync(CancellationToken.None); } catch (NpgsqlException) { }
        }
    }
}
