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

        -- ---- The operations surface ------------------------------------------------------

        -- Staff accounts. PBKDF2-SHA256 hashes; two roles, because this release has two kinds
        -- of action and a permission model with more entries than the actions it governs is
        -- a design document, not a control.
        CREATE TABLE IF NOT EXISTS staff_account (
            username      VARCHAR(64) PRIMARY KEY,
            password_hash TEXT        NOT NULL,
            role          VARCHAR(16) NOT NULL CHECK (role IN ('admin', 'support')),
            enabled       BOOLEAN     NOT NULL DEFAULT true,
            created_at    TIMESTAMPTZ NOT NULL
        );

        -- Bearer sessions, hashed. In Postgres rather than process memory so two replicas
        -- behind one Service agree on who is signed in; idle expiry is enforced on read.
        CREATE TABLE IF NOT EXISTS staff_session (
            token_hash   TEXT        PRIMARY KEY,
            username     VARCHAR(64) NOT NULL REFERENCES staff_account (username) ON DELETE CASCADE,
            created_at   TIMESTAMPTZ NOT NULL,
            last_seen_at TIMESTAMPTZ NOT NULL
        );

        -- Tickets became real before the page existed: the cap and the deduplication used to
        -- hold only within one process. Now the unique index holds the deduplication and a
        -- guard row per conversation, locked in the creating transaction, holds the cap.
        CREATE SEQUENCE IF NOT EXISTS ticket_number_seq START 4701;

        CREATE TABLE IF NOT EXISTS support_ticket (
            ticket_number   VARCHAR(20) PRIMARY KEY,
            conversation_id VARCHAR(64) NOT NULL,
            category        VARCHAR(16) NOT NULL,
            summary         TEXT        NOT NULL,
            order_number    TEXT,
            dedupe_key      TEXT        NOT NULL,
            state           VARCHAR(16) NOT NULL DEFAULT 'open'
                CHECK (state IN ('open', 'claimed', 'resolved', 'closed')),
            owner           VARCHAR(64),
            created_at      TIMESTAMPTZ NOT NULL,
            updated_at      TIMESTAMPTZ NOT NULL,
            -- Bumped by every change. A mutation carries the version it read, so two people
            -- acting on one stale page cannot both succeed.
            version         INTEGER     NOT NULL DEFAULT 1,
            UNIQUE (conversation_id, dedupe_key)
        );
        CREATE INDEX IF NOT EXISTS support_ticket_state_updated ON support_ticket (state, updated_at DESC);
        CREATE INDEX IF NOT EXISTS support_ticket_conversation ON support_ticket (conversation_id);

        CREATE TABLE IF NOT EXISTS ticket_conversation (
            conversation_id VARCHAR(64) PRIMARY KEY
        );

        -- Everything done to a ticket, in order, each with who and when. Append-only: nothing
        -- updates or deletes a row, and the admin never exposes a way to. The conclusion of a
        -- resolution lives here and never on the ticket row, so reopening carries nothing
        -- forward and every conclusion a ticket ever had stays in its history.
        CREATE TABLE IF NOT EXISTS ticket_event (
            id            BIGSERIAL   PRIMARY KEY,
            ticket_number VARCHAR(20) NOT NULL REFERENCES support_ticket (ticket_number),
            kind          VARCHAR(16) NOT NULL
                CHECK (kind IN ('created', 'claimed', 'assigned', 'released', 'resolved', 'closed', 'reopened', 'note')),
            actor         VARCHAR(64) NOT NULL,
            from_state    VARCHAR(16),
            to_state      VARCHAR(16),
            from_owner    VARCHAR(64),
            to_owner      VARCHAR(64),
            note          TEXT,
            version_after INTEGER     NOT NULL,
            occurred_at   TIMESTAMPTZ NOT NULL
        );
        CREATE INDEX IF NOT EXISTS ticket_event_ticket ON ticket_event (ticket_number, id);

        -- The operational record of a turn: what a customer asked, what happened, how it
        -- ended. chat_memory is the model's windowed context, not a record of outcomes, and
        -- the SSE stream is gone the moment the browser is. Snapshots, not references.
        CREATE TABLE IF NOT EXISTS conversation_turn (
            turn_id         UUID        PRIMARY KEY,
            conversation_id VARCHAR(64) NOT NULL,
            started_at      TIMESTAMPTZ NOT NULL,
            ended_at        TIMESTAMPTZ,
            outcome         VARCHAR(20) NOT NULL DEFAULT 'running',
            failure         TEXT,
            model           VARCHAR(80),
            model_calls     INTEGER     NOT NULL DEFAULT 0,
            input_tokens    BIGINT      NOT NULL DEFAULT 0,
            output_tokens   BIGINT      NOT NULL DEFAULT 0,
            cost_usd        DOUBLE PRECISION,
            trace_id        VARCHAR(64),
            question        TEXT        NOT NULL,
            answer          TEXT,
            retrieval       JSONB,
            tools           JSONB
        );
        CREATE INDEX IF NOT EXISTS conversation_turn_conversation ON conversation_turn (conversation_id, started_at);
        CREATE INDEX IF NOT EXISTS conversation_turn_started ON conversation_turn (started_at DESC);
        CREATE INDEX IF NOT EXISTS conversation_turn_running ON conversation_turn (started_at) WHERE outcome = 'running';

        CREATE TABLE IF NOT EXISTS turn_model_call (
            turn_id       UUID        NOT NULL REFERENCES conversation_turn (turn_id) ON DELETE CASCADE,
            seq           INTEGER     NOT NULL,
            model         VARCHAR(80) NOT NULL,
            input_tokens  BIGINT      NOT NULL,
            output_tokens BIGINT      NOT NULL,
            stop_reason   VARCHAR(32),
            failed        BOOLEAN     NOT NULL,
            PRIMARY KEY (turn_id, seq)
        );

        -- A flag on a recorded answer, and its handling. Closing feedback means the report has
        -- been handled, not that the customer's issue is resolved.
        CREATE TABLE IF NOT EXISTS answer_feedback (
            id              BIGSERIAL   PRIMARY KEY,
            turn_id         UUID        NOT NULL REFERENCES conversation_turn (turn_id),
            conversation_id VARCHAR(64) NOT NULL,
            issue           VARCHAR(16) NOT NULL CHECK (issue IN ('incorrect', 'incomplete', 'other')),
            note            TEXT,
            state           VARCHAR(8)  NOT NULL DEFAULT 'open' CHECK (state IN ('open', 'closed')),
            created_by      VARCHAR(64) NOT NULL,
            created_at      TIMESTAMPTZ NOT NULL,
            closed_by       VARCHAR(64),
            closed_at       TIMESTAMPTZ,
            conclusion      TEXT,
            version         INTEGER     NOT NULL DEFAULT 1
        );
        CREATE INDEX IF NOT EXISTS answer_feedback_state ON answer_feedback (state, created_at DESC);

        -- Who looked, and what was refused. A change to a ticket is its history; what
        -- ticket_event cannot hold is what did not change a ticket: a view, and a refusal.
        CREATE TABLE IF NOT EXISTS admin_audit (
            id          BIGSERIAL   PRIMARY KEY,
            occurred_at TIMESTAMPTZ NOT NULL,
            actor       VARCHAR(64) NOT NULL,
            action      VARCHAR(64) NOT NULL,
            object_type VARCHAR(32),
            object_id   VARCHAR(128),
            outcome     VARCHAR(16) NOT NULL,
            detail      TEXT
        );
        CREATE INDEX IF NOT EXISTS admin_audit_occurred ON admin_audit (occurred_at DESC);
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
