using System.Text.Json;
using Npgsql;
using NpgsqlTypes;

namespace CustomerService.Chat;

public sealed record ModelCallRecord(int Seq, string Model, long InputTokens, long OutputTokens, string StopReason, bool Failed);

public sealed record TurnClose(
    string Outcome, string? Failure, string? Answer, string? Model, IReadOnlyList<ModelCallRecord> Calls,
    long InputTokens, long OutputTokens, double? CostUsd, string? TraceId,
    IReadOnlyList<PassageSummary> Retrieval, IReadOnlyList<ToolSummary> Tools);

/// <summary>
/// The operational record of a turn, written where the turn executes rather than from the
/// event stream that feeds the browser. Two boundaries, deliberately asymmetric: the opening
/// record is written before the model is called and its failure fails the turn -- a model call
/// this service cannot account for is worse than a turn that did not happen; the closing record
/// runs in the same block that persists the partial reply, on a token detached from the
/// request, and its failure is logged rather than raised. By then the money is spent.
/// </summary>
public interface ITurnRecorder
{
    Task<Guid> OpenAsync(string conversationId, string question, DateTimeOffset startedAt, CancellationToken ct);
    Task CloseAsync(Guid turnId, TurnClose close, DateTimeOffset endedAt, CancellationToken ct);
}

public sealed class PostgresTurnRecorder(NpgsqlDataSource db) : ITurnRecorder
{
    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<Guid> OpenAsync(string conversationId, string question, DateTimeOffset startedAt, CancellationToken ct)
    {
        var id = Guid.NewGuid();
        await using var cmd = db.CreateCommand(
            "INSERT INTO conversation_turn (turn_id, conversation_id, started_at, outcome, question) VALUES ($1, $2, $3, 'running', $4)");
        cmd.Parameters.Add(new NpgsqlParameter { Value = id });
        cmd.Parameters.Add(new NpgsqlParameter { Value = conversationId });
        cmd.Parameters.Add(new NpgsqlParameter { Value = startedAt });
        cmd.Parameters.Add(new NpgsqlParameter { Value = question });
        await cmd.ExecuteNonQueryAsync(ct);
        return id;
    }

    public async Task CloseAsync(Guid turnId, TurnClose c, DateTimeOffset endedAt, CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        await using (var cmd = new NpgsqlCommand("""
            UPDATE conversation_turn SET ended_at = $2, outcome = $3, failure = $4, answer = $5, model = $6, model_calls = $7,
                   input_tokens = $8, output_tokens = $9, cost_usd = $10, trace_id = $11, retrieval = $12, tools = $13
            WHERE turn_id = $1
            """, conn, tx))
        {
            cmd.Parameters.Add(new NpgsqlParameter { Value = turnId });
            cmd.Parameters.Add(new NpgsqlParameter { Value = endedAt });
            cmd.Parameters.Add(new NpgsqlParameter { Value = c.Outcome });
            cmd.Parameters.Add(new NpgsqlParameter { Value = (object?)c.Failure ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Text });
            cmd.Parameters.Add(new NpgsqlParameter { Value = (object?)c.Answer ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Text });
            cmd.Parameters.Add(new NpgsqlParameter { Value = (object?)c.Model ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Varchar });
            cmd.Parameters.Add(new NpgsqlParameter { Value = c.Calls.Count });
            cmd.Parameters.Add(new NpgsqlParameter { Value = c.InputTokens });
            cmd.Parameters.Add(new NpgsqlParameter { Value = c.OutputTokens });
            cmd.Parameters.Add(new NpgsqlParameter { Value = (object?)c.CostUsd ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Double });
            cmd.Parameters.Add(new NpgsqlParameter { Value = (object?)c.TraceId ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Varchar });
            cmd.Parameters.Add(new NpgsqlParameter { Value = JsonSerializer.Serialize(c.Retrieval, Json), NpgsqlDbType = NpgsqlDbType.Jsonb });
            cmd.Parameters.Add(new NpgsqlParameter { Value = JsonSerializer.Serialize(c.Tools, Json), NpgsqlDbType = NpgsqlDbType.Jsonb });
            await cmd.ExecuteNonQueryAsync(ct);
        }
        foreach (var call in c.Calls)
        {
            await using var ins = new NpgsqlCommand(
                "INSERT INTO turn_model_call (turn_id, seq, model, input_tokens, output_tokens, stop_reason, failed) VALUES ($1, $2, $3, $4, $5, $6, $7) ON CONFLICT DO NOTHING", conn, tx);
            ins.Parameters.Add(new NpgsqlParameter { Value = turnId });
            ins.Parameters.Add(new NpgsqlParameter { Value = call.Seq });
            ins.Parameters.Add(new NpgsqlParameter { Value = call.Model });
            ins.Parameters.Add(new NpgsqlParameter { Value = call.InputTokens });
            ins.Parameters.Add(new NpgsqlParameter { Value = call.OutputTokens });
            ins.Parameters.Add(new NpgsqlParameter { Value = (object?)(call.StopReason.Length > 0 ? call.StopReason : null) ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Varchar });
            ins.Parameters.Add(new NpgsqlParameter { Value = call.Failed });
            await ins.ExecuteNonQueryAsync(ct);
        }
        await tx.CommitAsync(ct);
    }

    /// <summary>
    /// Marks turns that were still running when a process died as interrupted. Conservative
    /// about age, because another replica may legitimately still be running a recent one; a
    /// request cannot outlive its timeouts, so anything older than the bound is gone.
    /// </summary>
    public async Task<int> RecoverAsync(TimeSpan olderThan, CancellationToken ct)
    {
        await using var cmd = db.CreateCommand(
            "UPDATE conversation_turn SET outcome = 'interrupted', ended_at = now(), failure = 'process ended before the turn recorded an outcome' " +
            "WHERE outcome = 'running' AND started_at < now() - $1");
        cmd.Parameters.Add(new NpgsqlParameter { Value = olderThan });
        return await cmd.ExecuteNonQueryAsync(ct);
    }
}

/// <summary>For tests of the turn that are not about the record.</summary>
public sealed class NoTurnRecorder : ITurnRecorder
{
    public Task<Guid> OpenAsync(string conversationId, string question, DateTimeOffset startedAt, CancellationToken ct) => Task.FromResult(Guid.NewGuid());
    public Task CloseAsync(Guid turnId, TurnClose close, DateTimeOffset endedAt, CancellationToken ct) => Task.CompletedTask;
}
