using Npgsql;

namespace CustomerService.Tickets;

public enum TicketState { Open, Claimed, Resolved, Closed }

public static class TicketStates
{
    public static string Name(this TicketState s) => s switch
    {
        TicketState.Open => "open", TicketState.Claimed => "claimed", TicketState.Resolved => "resolved", _ => "closed",
    };
    public static TicketState Parse(string s) => s switch
    {
        "open" => TicketState.Open, "claimed" => TicketState.Claimed, "resolved" => TicketState.Resolved, "closed" => TicketState.Closed,
        _ => throw new ArgumentException($"unknown ticket state {s}"),
    };
    public static bool TryParse(string? s, out TicketState state)
    {
        try { state = Parse(s ?? ""); return true; } catch (ArgumentException) { state = TicketState.Open; return false; }
    }
}

public sealed record TicketRecord(
    string TicketNumber, string ConversationId, string Category, string Summary, string? OrderNumber,
    TicketState State, string? Owner, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, int Version);

public sealed record TicketEvent(
    long Id, string TicketNumber, string Kind, string Actor, string? FromState, string? ToState,
    string? FromOwner, string? ToOwner, string? Note, int VersionAfter, DateTimeOffset OccurredAt);

public sealed record TicketDetail(TicketRecord Ticket, IReadOnlyList<TicketEvent> History);

public sealed record TicketFilter(TicketState? State, string? Owner, string? ConversationId, DateTimeOffset? From, DateTimeOffset? To);

/// <summary>The page acted on a version the ticket has moved past. A 409: refresh and retry.</summary>
public sealed class TicketConflictException(string number, int expected, int actual)
    : Exception($"ticket {number} is at version {actual}, not {expected}");

/// <summary>The action is not allowed from this state or by this actor. A 422: the operator's mistake.</summary>
public sealed class TicketRuleException(string message) : Exception(message);

public sealed class TicketNotFoundException(string number) : Exception($"no ticket {number}");

/// <summary>What the AI's tool gets back from creating a ticket.</summary>
public sealed record TicketCreation(TicketRecord Ticket, bool Created, bool AlreadyExisted, bool Capped);

/// <summary>
/// Tickets in Postgres: the AI's creation path with its deduplication and cap, and the human
/// workflow on top. The cap is a guard row per conversation locked in the creating
/// transaction, because a unique index cannot enforce a count; the deduplication is a unique
/// index on the normalised summary. Both hold across replicas, which the in-memory table
/// this replaced could only claim within one process.
/// </summary>
public sealed class TicketStore(NpgsqlDataSource db, Func<DateTimeOffset>? clock = null)
{
    // A frustrated customer must not become three tickets in a human agent's queue.
    public const int MaxTicketsPerConversation = 3;
    public const string AssistantActor = "assistant";

    readonly Func<DateTimeOffset> now = clock ?? (() => DateTimeOffset.UtcNow);

    // ---- creation, by the tool -------------------------------------------------------

    public async Task<TicketCreation> CreateAsync(string conversationId, string summary, string category, string? orderNumber, CancellationToken ct)
    {
        var key = Normalise(summary);
        await using var conn = await db.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        // The guard row: created if absent, then locked, so the count below and the insert
        // after it are one atomic step against every other creator for this conversation.
        await Exec(conn, tx, "INSERT INTO ticket_conversation (conversation_id) VALUES ($1) ON CONFLICT DO NOTHING", ct, conversationId);
        await Exec(conn, tx, "SELECT conversation_id FROM ticket_conversation WHERE conversation_id = $1 FOR UPDATE", ct, conversationId);

        var existing = await ReadOne(conn, tx, "SELECT " + Columns + " FROM support_ticket WHERE conversation_id = $1 AND dedupe_key = $2", ct, conversationId, key);
        if (existing is not null)
        {
            await tx.CommitAsync(ct);
            return new TicketCreation(existing, Created: false, AlreadyExisted: true, Capped: false);
        }
        await using (var count = new NpgsqlCommand("SELECT count(*) FROM support_ticket WHERE conversation_id = $1", conn, tx))
        {
            count.Parameters.Add(new NpgsqlParameter { Value = conversationId });
            if (Convert.ToInt32(await count.ExecuteScalarAsync(ct)) >= MaxTicketsPerConversation)
            {
                await tx.CommitAsync(ct);
                return new TicketCreation(null!, Created: false, AlreadyExisted: false, Capped: true);
            }
        }

        var at = now();
        string number;
        await using (var seq = new NpgsqlCommand("SELECT nextval('ticket_number_seq')", conn, tx))
            number = $"TKT-{Convert.ToInt64(await seq.ExecuteScalarAsync(ct))}";
        await using (var ins = new NpgsqlCommand(
            "INSERT INTO support_ticket (ticket_number, conversation_id, category, summary, order_number, dedupe_key, state, owner, created_at, updated_at, version) " +
            "VALUES ($1, $2, $3, $4, $5, $6, 'open', NULL, $7, $7, 1)", conn, tx))
        {
            ins.Parameters.Add(new NpgsqlParameter { Value = number });
            ins.Parameters.Add(new NpgsqlParameter { Value = conversationId });
            ins.Parameters.Add(new NpgsqlParameter { Value = NormaliseCategory(category) });
            ins.Parameters.Add(new NpgsqlParameter { Value = summary });
            ins.Parameters.Add(new NpgsqlParameter { Value = (object?)(string.IsNullOrWhiteSpace(orderNumber) ? null : orderNumber.Trim()) ?? DBNull.Value, NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Text });
            ins.Parameters.Add(new NpgsqlParameter { Value = key });
            ins.Parameters.Add(new NpgsqlParameter { Value = at });
            await ins.ExecuteNonQueryAsync(ct);
        }
        await InsertEvent(conn, tx, number, "created", AssistantActor, null, "open", null, null, null, 1, at, ct);
        await tx.CommitAsync(ct);
        var ticket = new TicketRecord(number, conversationId, NormaliseCategory(category), summary,
            string.IsNullOrWhiteSpace(orderNumber) ? null : orderNumber.Trim(), TicketState.Open, null, at, at, 1);
        return new TicketCreation(ticket, Created: true, AlreadyExisted: false, Capped: false);
    }

    // ---- the workflow, by people -----------------------------------------------------

    /// <summary>One transaction shape for every change: lock the row, check the version, apply the rule, write the event.</summary>
    async Task<TicketDetail> ChangeAsync(string number, int expectedVersion, Func<TicketRecord, (string kind, TicketState to, string? owner, string? note)> rule, string actor, CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        var t = await ReadOne(conn, tx, "SELECT " + Columns + " FROM support_ticket WHERE ticket_number = $1 FOR UPDATE", ct, number)
            ?? throw new TicketNotFoundException(number);
        if (t.Version != expectedVersion) throw new TicketConflictException(number, expectedVersion, t.Version);

        var (kind, to, owner, note) = rule(t);
        var at = now();
        var version = t.Version + 1;
        await using (var upd = new NpgsqlCommand("UPDATE support_ticket SET state = $2, owner = $3, updated_at = $4, version = $5 WHERE ticket_number = $1", conn, tx))
        {
            upd.Parameters.Add(new NpgsqlParameter { Value = number });
            upd.Parameters.Add(new NpgsqlParameter { Value = to.Name() });
            upd.Parameters.Add(new NpgsqlParameter { Value = (object?)owner ?? DBNull.Value, NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Varchar });
            upd.Parameters.Add(new NpgsqlParameter { Value = at });
            upd.Parameters.Add(new NpgsqlParameter { Value = version });
            await upd.ExecuteNonQueryAsync(ct);
        }
        await InsertEvent(conn, tx, number, kind, actor, t.State.Name(), to.Name(), t.Owner, owner, note, version, at, ct);
        await tx.CommitAsync(ct);
        return await GetAsync(number, ct) ?? throw new TicketNotFoundException(number);
    }

    static void RequireOwnerOrAdmin(TicketRecord t, Admin.Actor actor, string verb)
    {
        if (!actor.IsAdmin && !string.Equals(t.Owner, actor.Username, StringComparison.Ordinal))
            throw new TicketRuleException($"only the owner or an admin can {verb} this ticket");
    }

    /// <summary>First come, first served on an unowned open ticket.</summary>
    public Task<TicketDetail> ClaimAsync(string number, int expectedVersion, Admin.Actor actor, CancellationToken ct) =>
        ChangeAsync(number, expectedVersion, t =>
        {
            if (t.State != TicketState.Open) throw new TicketRuleException($"a {t.State.Name()} ticket cannot be claimed");
            if (t.Owner is not null) throw new TicketRuleException($"the ticket is already owned by {t.Owner}");
            return ("claimed", TicketState.Claimed, actor.Username, null);
        }, actor.Username, ct);

    /// <summary>Assign to someone: an admin may, or the current owner may hand it over.</summary>
    public Task<TicketDetail> AssignAsync(string number, int expectedVersion, string assignee, Admin.Actor actor, CancellationToken ct) =>
        ChangeAsync(number, expectedVersion, t =>
        {
            if (t.State is TicketState.Resolved or TicketState.Closed) throw new TicketRuleException($"a {t.State.Name()} ticket cannot be assigned");
            if (t.Owner is not null) RequireOwnerOrAdmin(t, actor, "reassign");
            else if (!actor.IsAdmin) throw new TicketRuleException("only an admin can assign an unowned ticket to someone else; claim it instead");
            if (string.IsNullOrWhiteSpace(assignee)) throw new TicketRuleException("an assignee is required");
            return ("assigned", TicketState.Claimed, assignee.Trim(), null);
        }, actor.Username, ct);

    /// <summary>Back to the queue, unowned.</summary>
    public Task<TicketDetail> ReleaseAsync(string number, int expectedVersion, Admin.Actor actor, CancellationToken ct) =>
        ChangeAsync(number, expectedVersion, t =>
        {
            if (t.State != TicketState.Claimed) throw new TicketRuleException($"a {t.State.Name()} ticket cannot be released");
            RequireOwnerOrAdmin(t, actor, "release");
            return ("released", TicketState.Open, null, null);
        }, actor.Username, ct);

    /// <summary>Resolving requires a conclusion, stored on the event and never on the row.</summary>
    public Task<TicketDetail> ResolveAsync(string number, int expectedVersion, string? conclusion, Admin.Actor actor, CancellationToken ct) =>
        ChangeAsync(number, expectedVersion, t =>
        {
            if (t.State != TicketState.Claimed) throw new TicketRuleException($"a {t.State.Name()} ticket cannot be resolved; claim it first");
            RequireOwnerOrAdmin(t, actor, "resolve");
            if (string.IsNullOrWhiteSpace(conclusion)) throw new TicketRuleException("resolving requires a conclusion: what was done for the customer");
            return ("resolved", TicketState.Resolved, t.Owner, conclusion.Trim());
        }, actor.Username, ct);

    public Task<TicketDetail> CloseAsync(string number, int expectedVersion, string? note, Admin.Actor actor, CancellationToken ct) =>
        ChangeAsync(number, expectedVersion, t =>
        {
            if (t.State is not (TicketState.Claimed or TicketState.Resolved)) throw new TicketRuleException($"a {t.State.Name()} ticket cannot be closed");
            RequireOwnerOrAdmin(t, actor, "close");
            return ("closed", TicketState.Closed, t.Owner, string.IsNullOrWhiteSpace(note) ? null : note.Trim());
        }, actor.Username, ct);

    /// <summary>
    /// Reopening clears the owner -- a reopened ticket is nobody's until claimed again -- and
    /// requires a reason, because a ticket that comes back is the interesting case.
    /// </summary>
    public Task<TicketDetail> ReopenAsync(string number, int expectedVersion, string? reason, Admin.Actor actor, CancellationToken ct) =>
        ChangeAsync(number, expectedVersion, t =>
        {
            if (t.State is not (TicketState.Resolved or TicketState.Closed)) throw new TicketRuleException($"a {t.State.Name()} ticket cannot be reopened");
            if (string.IsNullOrWhiteSpace(reason)) throw new TicketRuleException("reopening requires a reason");
            return ("reopened", TicketState.Open, null, reason.Trim());
        }, actor.Username, ct);

    public Task<TicketDetail> NoteAsync(string number, int expectedVersion, string? text, Admin.Actor actor, CancellationToken ct) =>
        ChangeAsync(number, expectedVersion, t =>
        {
            if (string.IsNullOrWhiteSpace(text)) throw new TicketRuleException("a note needs text");
            return ("note", t.State, t.Owner, text.Trim());
        }, actor.Username, ct);

    // ---- reads -------------------------------------------------------------------------

    public async Task<TicketDetail?> GetAsync(string number, CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        var t = await ReadOne(conn, null, "SELECT " + Columns + " FROM support_ticket WHERE ticket_number = $1", ct, number);
        if (t is null) return null;
        await using var cmd = new NpgsqlCommand("SELECT id, ticket_number, kind, actor, from_state, to_state, from_owner, to_owner, note, version_after, occurred_at FROM ticket_event WHERE ticket_number = $1 ORDER BY id", conn);
        cmd.Parameters.Add(new NpgsqlParameter { Value = number });
        await using var r = await cmd.ExecuteReaderAsync(ct);
        var history = new List<TicketEvent>();
        while (await r.ReadAsync(ct))
            history.Add(new TicketEvent(r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetString(3), Str(r, 4), Str(r, 5), Str(r, 6), Str(r, 7), Str(r, 8), r.GetInt32(9), r.GetFieldValue<DateTimeOffset>(10)));
        return new TicketDetail(t, history);
    }

    public async Task<Admin.Page<TicketRecord>> SearchAsync(TicketFilter f, int page, int size, CancellationToken ct)
    {
        const string where = """
            WHERE ($1 = '' OR state = $1) AND ($2 = '' OR owner = $2) AND ($3 = '' OR conversation_id = $3)
              AND ($4::timestamptz IS NULL OR created_at >= $4) AND ($5::timestamptz IS NULL OR created_at < $5)
            """;
        void Bind(NpgsqlCommand c)
        {
            c.Parameters.Add(new NpgsqlParameter { Value = f.State?.Name() ?? "" });
            c.Parameters.Add(new NpgsqlParameter { Value = f.Owner ?? "" });
            c.Parameters.Add(new NpgsqlParameter { Value = f.ConversationId ?? "" });
            c.Parameters.Add(new NpgsqlParameter { Value = (object?)f.From ?? DBNull.Value, NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.TimestampTz });
            c.Parameters.Add(new NpgsqlParameter { Value = (object?)f.To ?? DBNull.Value, NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.TimestampTz });
        }
        await using var conn = await db.OpenConnectionAsync(ct);
        await using var count = new NpgsqlCommand("SELECT count(*) FROM support_ticket " + where, conn);
        Bind(count);
        var total = Convert.ToInt64(await count.ExecuteScalarAsync(ct));
        await using var cmd = new NpgsqlCommand("SELECT " + Columns + " FROM support_ticket " + where + " ORDER BY updated_at DESC, ticket_number DESC LIMIT $6 OFFSET $7", conn);
        Bind(cmd);
        cmd.Parameters.Add(new NpgsqlParameter { Value = size });
        cmd.Parameters.Add(new NpgsqlParameter { Value = (page - 1) * size });
        await using var r = await cmd.ExecuteReaderAsync(ct);
        var items = new List<TicketRecord>();
        while (await r.ReadAsync(ct)) items.Add(Map(r));
        return new Admin.Page<TicketRecord>(items, page, size, total);
    }

    public async Task<IReadOnlyList<TicketRecord>> ForConversationAsync(string conversationId, CancellationToken ct)
    {
        await using var cmd = db.CreateCommand("SELECT " + Columns + " FROM support_ticket WHERE conversation_id = $1 ORDER BY created_at");
        cmd.Parameters.Add(new NpgsqlParameter { Value = conversationId });
        await using var r = await cmd.ExecuteReaderAsync(ct);
        var items = new List<TicketRecord>();
        while (await r.ReadAsync(ct)) items.Add(Map(r));
        return items;
    }

    public async Task<long> CountAsync(TicketState state, CancellationToken ct)
    {
        await using var cmd = db.CreateCommand("SELECT count(*) FROM support_ticket WHERE state = $1");
        cmd.Parameters.Add(new NpgsqlParameter { Value = state.Name() });
        return Convert.ToInt64(await cmd.ExecuteScalarAsync(ct));
    }

    // ---- plumbing ------------------------------------------------------------------------

    const string Columns = "ticket_number, conversation_id, category, summary, order_number, state, owner, created_at, updated_at, version";

    static TicketRecord Map(NpgsqlDataReader r) => new(
        r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3), Str(r, 4), TicketStates.Parse(r.GetString(5)), Str(r, 6),
        r.GetFieldValue<DateTimeOffset>(7), r.GetFieldValue<DateTimeOffset>(8), r.GetInt32(9));

    static string? Str(NpgsqlDataReader r, int i) => r.IsDBNull(i) ? null : r.GetString(i);

    static async Task Exec(NpgsqlConnection conn, NpgsqlTransaction tx, string sql, CancellationToken ct, params object[] args)
    {
        await using var cmd = new NpgsqlCommand(sql, conn, tx);
        foreach (var a in args) cmd.Parameters.Add(new NpgsqlParameter { Value = a });
        await cmd.ExecuteNonQueryAsync(ct);
    }

    static async Task<TicketRecord?> ReadOne(NpgsqlConnection conn, NpgsqlTransaction? tx, string sql, CancellationToken ct, params object[] args)
    {
        await using var cmd = new NpgsqlCommand(sql, conn, tx);
        foreach (var a in args) cmd.Parameters.Add(new NpgsqlParameter { Value = a });
        await using var r = await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct) ? Map(r) : null;
    }

    static async Task InsertEvent(NpgsqlConnection conn, NpgsqlTransaction tx, string number, string kind, string actor, string? from, string? to,
        string? fromOwner, string? toOwner, string? note, int versionAfter, DateTimeOffset at, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(
            "INSERT INTO ticket_event (ticket_number, kind, actor, from_state, to_state, from_owner, to_owner, note, version_after, occurred_at) " +
            "VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10)", conn, tx);
        cmd.Parameters.Add(new NpgsqlParameter { Value = number });
        cmd.Parameters.Add(new NpgsqlParameter { Value = kind });
        cmd.Parameters.Add(new NpgsqlParameter { Value = actor });
        foreach (var s in new[] { from, to, fromOwner, toOwner })
            cmd.Parameters.Add(new NpgsqlParameter { Value = (object?)s ?? DBNull.Value, NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Varchar });
        cmd.Parameters.Add(new NpgsqlParameter { Value = (object?)note ?? DBNull.Value, NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Text });
        cmd.Parameters.Add(new NpgsqlParameter { Value = versionAfter });
        cmd.Parameters.Add(new NpgsqlParameter { Value = at });
        await cmd.ExecuteNonQueryAsync(ct);
    }

    internal static string Normalise(string s) =>
        string.Join(' ', s.Trim().ToLowerInvariant().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    internal static string NormaliseCategory(string category) => Normalise(category) switch
    {
        "returns" or "shipping" or "payment" or "account" => Normalise(category),
        _ => "other",
    };
}
