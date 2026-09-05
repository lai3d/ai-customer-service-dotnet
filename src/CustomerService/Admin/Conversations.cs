using System.Text.Json;
using CustomerService.Chat;
using CustomerService.Tickets;
using Npgsql;
using NpgsqlTypes;

namespace CustomerService.Admin;

public sealed record ConversationSummary(
    string ConversationId, DateTimeOffset FirstTurnAt, DateTimeOffset LastTurnAt, int Turns, string LastOutcome,
    long InputTokens, long OutputTokens, int Tickets, int OpenFeedback);

public sealed record TranscriptMessage(string Role, string Content, DateTimeOffset CreatedAt);

public sealed record TurnView(
    Guid TurnId, DateTimeOffset StartedAt, DateTimeOffset? EndedAt, string Outcome, string? Failure,
    string Question, string? Answer, string? Model, int ModelCalls, long InputTokens, long OutputTokens, double? CostUsd,
    string? TraceId, IReadOnlyList<PassageSummary> Retrieval, IReadOnlyList<ToolSummary> Tools,
    IReadOnlyList<ModelCallRecord> Calls, IReadOnlyList<FeedbackRecord> Feedback);

public sealed record ConversationDetail(
    string ConversationId, IReadOnlyList<TranscriptMessage> Messages, IReadOnlyList<TurnView> Turns, IReadOnlyList<TicketRecord> Tickets);

public sealed record ConversationFilter(string? Query, string? Outcome, DateTimeOffset? From, DateTimeOffset? To);

public sealed record Overview(
    long Turns, IReadOnlyDictionary<string, long> ByOutcome, long InputTokens, long OutputTokens, double CostUsd,
    long UnpricedTurns, long OpenTickets, long ClaimedTickets, long OpenFeedback, DateTimeOffset Since);

/// <summary>
/// What an operator is asked: did this fail or did the customer close the tab, what did
/// retrieval return, what did it cost. Answered from the turn record, not from chat memory --
/// memory is the model's windowed context and a history that disappears when the window
/// slides is not a history.
/// </summary>
public sealed class Conversations(NpgsqlDataSource db, TicketStore tickets, Feedback feedback)
{
    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<Page<ConversationSummary>> SearchAsync(ConversationFilter f, int page, int size, CancellationToken ct)
    {
        const string grouped = """
            SELECT t.conversation_id, min(t.started_at) AS first_at, max(t.started_at) AS last_at, count(*)::int AS turns,
                   (array_agg(t.outcome ORDER BY t.started_at DESC))[1] AS last_outcome,
                   sum(t.input_tokens) AS input_tokens, sum(t.output_tokens) AS output_tokens,
                   (SELECT count(*)::int FROM support_ticket s WHERE s.conversation_id = t.conversation_id) AS tickets,
                   (SELECT count(*)::int FROM answer_feedback a WHERE a.conversation_id = t.conversation_id AND a.state = 'open') AS open_feedback
            FROM conversation_turn t
            WHERE ($1 = '' OR t.conversation_id LIKE $1 || '%')
              AND ($2::timestamptz IS NULL OR t.started_at >= $2) AND ($3::timestamptz IS NULL OR t.started_at < $3)
            GROUP BY t.conversation_id
            """;
        var where = "WHERE ($4 = '' OR g.last_outcome = $4)";
        void Bind(NpgsqlCommand c)
        {
            c.Parameters.Add(new NpgsqlParameter { Value = f.Query ?? "" });
            c.Parameters.Add(new NpgsqlParameter { Value = (object?)f.From ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.TimestampTz });
            c.Parameters.Add(new NpgsqlParameter { Value = (object?)f.To ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.TimestampTz });
            c.Parameters.Add(new NpgsqlParameter { Value = f.Outcome ?? "" });
        }
        await using var conn = await db.OpenConnectionAsync(ct);
        await using var count = new NpgsqlCommand($"SELECT count(*) FROM ({grouped}) g {where}", conn);
        Bind(count);
        var total = Convert.ToInt64(await count.ExecuteScalarAsync(ct));
        await using var cmd = new NpgsqlCommand($"SELECT * FROM ({grouped}) g {where} ORDER BY g.last_at DESC LIMIT $5 OFFSET $6", conn);
        Bind(cmd);
        cmd.Parameters.Add(new NpgsqlParameter { Value = size });
        cmd.Parameters.Add(new NpgsqlParameter { Value = (page - 1) * size });
        await using var r = await cmd.ExecuteReaderAsync(ct);
        var items = new List<ConversationSummary>();
        while (await r.ReadAsync(ct))
            items.Add(new ConversationSummary(r.GetString(0), r.GetFieldValue<DateTimeOffset>(1), r.GetFieldValue<DateTimeOffset>(2), r.GetInt32(3),
                r.GetString(4), r.GetInt64(5), r.GetInt64(6), r.GetInt32(7), r.GetInt32(8)));
        return new Page<ConversationSummary>(items, page, size, total);
    }

    /// <summary>Null when nothing was ever recorded for the id.</summary>
    public async Task<ConversationDetail?> GetAsync(string conversationId, CancellationToken ct)
    {
        var turns = await TurnsAsync(conversationId, ct);
        var messages = await MessagesAsync(conversationId, ct);
        if (turns.Count == 0 && messages.Count == 0) return null;
        var tk = await tickets.ForConversationAsync(conversationId, ct);
        return new ConversationDetail(conversationId, messages, turns, tk);
    }

    async Task<IReadOnlyList<TranscriptMessage>> MessagesAsync(string conversationId, CancellationToken ct)
    {
        await using var cmd = db.CreateCommand("SELECT role, content, created_at FROM chat_memory WHERE conversation_id = $1 ORDER BY id");
        cmd.Parameters.Add(new NpgsqlParameter { Value = conversationId });
        await using var r = await cmd.ExecuteReaderAsync(ct);
        var list = new List<TranscriptMessage>();
        while (await r.ReadAsync(ct)) list.Add(new TranscriptMessage(r.GetString(0), r.GetString(1), r.GetFieldValue<DateTimeOffset>(2)));
        return list;
    }

    async Task<IReadOnlyList<TurnView>> TurnsAsync(string conversationId, CancellationToken ct)
    {
        var fb = await feedback.ForConversationAsync(conversationId, ct);
        var calls = new Dictionary<Guid, List<ModelCallRecord>>();
        await using (var cc = db.CreateCommand("SELECT c.turn_id, c.seq, c.model, c.input_tokens, c.output_tokens, c.stop_reason, c.failed FROM turn_model_call c JOIN conversation_turn t ON t.turn_id = c.turn_id WHERE t.conversation_id = $1 ORDER BY c.turn_id, c.seq"))
        {
            cc.Parameters.Add(new NpgsqlParameter { Value = conversationId });
            await using var rr = await cc.ExecuteReaderAsync(ct);
            while (await rr.ReadAsync(ct))
            {
                var id = rr.GetGuid(0);
                if (!calls.TryGetValue(id, out var list)) calls[id] = list = new();
                list.Add(new ModelCallRecord(rr.GetInt32(1), rr.GetString(2), rr.GetInt64(3), rr.GetInt64(4), rr.IsDBNull(5) ? "" : rr.GetString(5), rr.GetBoolean(6)));
            }
        }
        await using var cmd = db.CreateCommand("""
            SELECT turn_id, started_at, ended_at, outcome, failure, question, answer, model, model_calls, input_tokens, output_tokens, cost_usd, trace_id, retrieval, tools
            FROM conversation_turn WHERE conversation_id = $1 ORDER BY started_at
            """);
        cmd.Parameters.Add(new NpgsqlParameter { Value = conversationId });
        await using var r = await cmd.ExecuteReaderAsync(ct);
        var turns = new List<TurnView>();
        while (await r.ReadAsync(ct))
        {
            var id = r.GetGuid(0);
            turns.Add(new TurnView(id, r.GetFieldValue<DateTimeOffset>(1), r.IsDBNull(2) ? null : r.GetFieldValue<DateTimeOffset>(2), r.GetString(3),
                r.IsDBNull(4) ? null : r.GetString(4), r.GetString(5), r.IsDBNull(6) ? null : r.GetString(6), r.IsDBNull(7) ? null : r.GetString(7),
                r.GetInt32(8), r.GetInt64(9), r.GetInt64(10), r.IsDBNull(11) ? null : r.GetDouble(11), r.IsDBNull(12) ? null : r.GetString(12),
                r.IsDBNull(13) ? [] : JsonSerializer.Deserialize<List<PassageSummary>>(r.GetString(13), Json) ?? [],
                r.IsDBNull(14) ? [] : JsonSerializer.Deserialize<List<ToolSummary>>(r.GetString(14), Json) ?? [],
                calls.TryGetValue(id, out var cl) ? cl : [],
                fb.Where(x => x.TurnId == id).ToList()));
        }
        return turns;
    }

    public async Task<bool> TurnExistsAsync(Guid turnId, string conversationId, CancellationToken ct)
    {
        await using var cmd = db.CreateCommand("SELECT 1 FROM conversation_turn WHERE turn_id = $1 AND conversation_id = $2");
        cmd.Parameters.Add(new NpgsqlParameter { Value = turnId });
        cmd.Parameters.Add(new NpgsqlParameter { Value = conversationId });
        return await cmd.ExecuteScalarAsync(ct) is not null;
    }

    /// <summary>
    /// Totals over a window. Cost is an estimate, and it says when it is incomplete: a turn on a
    /// model with no price contributes its tokens and no cost, and is counted rather than
    /// quietly omitted.
    /// </summary>
    public async Task<Overview> OverviewAsync(TimeSpan window, CancellationToken ct)
    {
        var since = DateTimeOffset.UtcNow - window;
        var byOutcome = new Dictionary<string, long>();
        long turns = 0, inTok = 0, outTok = 0, unpriced = 0; double cost = 0;
        await using (var cmd = db.CreateCommand("SELECT outcome, count(*), sum(input_tokens), sum(output_tokens), coalesce(sum(cost_usd), 0), count(*) FILTER (WHERE cost_usd IS NULL AND model_calls > 0) FROM conversation_turn WHERE started_at >= $1 GROUP BY outcome"))
        {
            cmd.Parameters.Add(new NpgsqlParameter { Value = since });
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                var n = r.GetInt64(1);
                byOutcome[r.GetString(0)] = n; turns += n; inTok += r.GetInt64(2); outTok += r.GetInt64(3); cost += r.GetDouble(4); unpriced += r.GetInt64(5);
            }
        }
        return new Overview(turns, byOutcome, inTok, outTok, Math.Round(cost, 6), unpriced,
            await tickets.CountAsync(TicketState.Open, ct), await tickets.CountAsync(TicketState.Claimed, ct), await feedback.CountOpenAsync(ct), since);
    }
}

public sealed record FeedbackRecord(
    long Id, Guid TurnId, string ConversationId, string Issue, string? Note, string State, string CreatedBy, DateTimeOffset CreatedAt,
    string? ClosedBy, DateTimeOffset? ClosedAt, string? Conclusion, int Version);

public sealed class FeedbackConflictException(long id) : Exception($"feedback {id} has changed; refresh and retry");
public sealed class FeedbackRuleException(string message) : Exception(message);

/// <summary>A flag on a recorded answer. Closing it means the report was handled, not that the customer's issue is resolved.</summary>
public sealed class Feedback(NpgsqlDataSource db)
{
    public static readonly string[] Issues = ["incorrect", "incomplete", "other"];

    public async Task<FeedbackRecord> CreateAsync(Guid turnId, string conversationId, string issue, string? note, string actor, CancellationToken ct)
    {
        if (!Issues.Contains(issue)) throw new FeedbackRuleException("issue must be one of incorrect, incomplete, other");
        await using var cmd = db.CreateCommand("""
            INSERT INTO answer_feedback (turn_id, conversation_id, issue, note, state, created_by, created_at, version)
            VALUES ($1, $2, $3, $4, 'open', $5, now(), 1) RETURNING id, created_at
            """);
        cmd.Parameters.Add(new NpgsqlParameter { Value = turnId });
        cmd.Parameters.Add(new NpgsqlParameter { Value = conversationId });
        cmd.Parameters.Add(new NpgsqlParameter { Value = issue });
        cmd.Parameters.Add(new NpgsqlParameter { Value = (object?)(string.IsNullOrWhiteSpace(note) ? null : note.Trim()) ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Text });
        cmd.Parameters.Add(new NpgsqlParameter { Value = actor });
        await using var r = await cmd.ExecuteReaderAsync(ct);
        await r.ReadAsync(ct);
        return new FeedbackRecord(r.GetInt64(0), turnId, conversationId, issue, string.IsNullOrWhiteSpace(note) ? null : note.Trim(), "open", actor, r.GetFieldValue<DateTimeOffset>(1), null, null, null, 1);
    }

    public async Task<FeedbackRecord> CloseAsync(long id, int expectedVersion, string? conclusion, string actor, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(conclusion)) throw new FeedbackRuleException("closing feedback requires a conclusion");
        await using var cmd = db.CreateCommand("""
            UPDATE answer_feedback SET state = 'closed', closed_by = $3, closed_at = now(), conclusion = $4, version = version + 1
            WHERE id = $1 AND version = $2 AND state = 'open'
            """);
        cmd.Parameters.Add(new NpgsqlParameter { Value = id });
        cmd.Parameters.Add(new NpgsqlParameter { Value = expectedVersion });
        cmd.Parameters.Add(new NpgsqlParameter { Value = actor });
        cmd.Parameters.Add(new NpgsqlParameter { Value = conclusion.Trim() });
        if (await cmd.ExecuteNonQueryAsync(ct) == 0)
        {
            var current = await GetAsync(id, ct) ?? throw new FeedbackRuleException($"no feedback {id}");
            if (current.State == "closed") throw new FeedbackRuleException("the feedback is already closed");
            throw new FeedbackConflictException(id);
        }
        return (await GetAsync(id, ct))!;
    }

    public async Task<FeedbackRecord?> GetAsync(long id, CancellationToken ct)
    {
        await using var cmd = db.CreateCommand("SELECT " + Columns + " FROM answer_feedback WHERE id = $1");
        cmd.Parameters.Add(new NpgsqlParameter { Value = id });
        await using var r = await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct) ? Map(r) : null;
    }

    public async Task<Page<FeedbackRecord>> SearchAsync(string? state, int page, int size, CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        await using var count = new NpgsqlCommand("SELECT count(*) FROM answer_feedback WHERE ($1 = '' OR state = $1)", conn);
        count.Parameters.Add(new NpgsqlParameter { Value = state ?? "" });
        var total = Convert.ToInt64(await count.ExecuteScalarAsync(ct));
        await using var cmd = new NpgsqlCommand("SELECT " + Columns + " FROM answer_feedback WHERE ($1 = '' OR state = $1) ORDER BY created_at DESC, id DESC LIMIT $2 OFFSET $3", conn);
        cmd.Parameters.Add(new NpgsqlParameter { Value = state ?? "" });
        cmd.Parameters.Add(new NpgsqlParameter { Value = size });
        cmd.Parameters.Add(new NpgsqlParameter { Value = (page - 1) * size });
        await using var r = await cmd.ExecuteReaderAsync(ct);
        var items = new List<FeedbackRecord>();
        while (await r.ReadAsync(ct)) items.Add(Map(r));
        return new Page<FeedbackRecord>(items, page, size, total);
    }

    public async Task<IReadOnlyList<FeedbackRecord>> ForConversationAsync(string conversationId, CancellationToken ct)
    {
        await using var cmd = db.CreateCommand("SELECT " + Columns + " FROM answer_feedback WHERE conversation_id = $1 ORDER BY id");
        cmd.Parameters.Add(new NpgsqlParameter { Value = conversationId });
        await using var r = await cmd.ExecuteReaderAsync(ct);
        var items = new List<FeedbackRecord>();
        while (await r.ReadAsync(ct)) items.Add(Map(r));
        return items;
    }

    public async Task<long> CountOpenAsync(CancellationToken ct)
    {
        await using var cmd = db.CreateCommand("SELECT count(*) FROM answer_feedback WHERE state = 'open'");
        return Convert.ToInt64(await cmd.ExecuteScalarAsync(ct));
    }

    const string Columns = "id, turn_id, conversation_id, issue, note, state, created_by, created_at, closed_by, closed_at, conclusion, version";
    static FeedbackRecord Map(NpgsqlDataReader r) => new(r.GetInt64(0), r.GetGuid(1), r.GetString(2), r.GetString(3), r.IsDBNull(4) ? null : r.GetString(4),
        r.GetString(5), r.GetString(6), r.GetFieldValue<DateTimeOffset>(7), r.IsDBNull(8) ? null : r.GetString(8),
        r.IsDBNull(9) ? null : r.GetFieldValue<DateTimeOffset>(9), r.IsDBNull(10) ? null : r.GetString(10), r.GetInt32(11));
}
