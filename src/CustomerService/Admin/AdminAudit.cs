using Npgsql;

namespace CustomerService.Admin;

public sealed record AuditEvent(long Id, DateTimeOffset OccurredAt, string Actor, string Action, string? ObjectType, string? ObjectId, string Outcome, string? Detail);

/// <summary>
/// Who looked, and what was refused. Opening a conversation is recorded because looking is the
/// sensitive operation on this surface; a refused action is recorded because an audit trail
/// of what succeeded is missing exactly the rows an investigation opens it for. There is no
/// endpoint that edits or deletes this table.
/// </summary>
public sealed class AdminAudit(NpgsqlDataSource db)
{
    public async Task RecordAsync(string actor, string action, string? objectType, string? objectId, string outcome, string? detail, CancellationToken ct)
    {
        await using var cmd = db.CreateCommand(
            "INSERT INTO admin_audit (occurred_at, actor, action, object_type, object_id, outcome, detail) VALUES (now(), $1, $2, $3, $4, $5, $6)");
        cmd.Parameters.Add(new NpgsqlParameter { Value = actor });
        cmd.Parameters.Add(new NpgsqlParameter { Value = action });
        cmd.Parameters.Add(new NpgsqlParameter { Value = (object?)objectType ?? DBNull.Value, NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Varchar });
        cmd.Parameters.Add(new NpgsqlParameter { Value = (object?)objectId ?? DBNull.Value, NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Varchar });
        cmd.Parameters.Add(new NpgsqlParameter { Value = outcome });
        cmd.Parameters.Add(new NpgsqlParameter { Value = (object?)detail ?? DBNull.Value, NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Text });
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<Page<AuditEvent>> SearchAsync(string? actor, string? action, int page, int size, CancellationToken ct)
    {
        const string where = "WHERE ($1 = '' OR actor = $1) AND ($2 = '' OR action = $2)";
        await using var count = db.CreateCommand($"SELECT count(*) FROM admin_audit {where}");
        count.Parameters.Add(new NpgsqlParameter { Value = actor ?? "" });
        count.Parameters.Add(new NpgsqlParameter { Value = action ?? "" });
        var total = Convert.ToInt64(await count.ExecuteScalarAsync(ct));

        await using var cmd = db.CreateCommand($"SELECT id, occurred_at, actor, action, object_type, object_id, outcome, detail FROM admin_audit {where} ORDER BY id DESC LIMIT $3 OFFSET $4");
        cmd.Parameters.Add(new NpgsqlParameter { Value = actor ?? "" });
        cmd.Parameters.Add(new NpgsqlParameter { Value = action ?? "" });
        cmd.Parameters.Add(new NpgsqlParameter { Value = size });
        cmd.Parameters.Add(new NpgsqlParameter { Value = (page - 1) * size });
        await using var r = await cmd.ExecuteReaderAsync(ct);
        var items = new List<AuditEvent>();
        while (await r.ReadAsync(ct))
            items.Add(new AuditEvent(r.GetInt64(0), r.GetFieldValue<DateTimeOffset>(1), r.GetString(2), r.GetString(3),
                r.IsDBNull(4) ? null : r.GetString(4), r.IsDBNull(5) ? null : r.GetString(5), r.GetString(6), r.IsDBNull(7) ? null : r.GetString(7)));
        return new Page<AuditEvent>(items, page, size, total);
    }
}

/// <summary>One page of a bounded, stably ordered list.</summary>
public sealed record Page<T>(IReadOnlyList<T> Items, int PageNumber, int Size, long Total);

public static class Paging
{
    public const int MaxSize = 100;
    public static (int page, int size) Bound(int? page, int? size) =>
        (Math.Max(1, page ?? 1), Math.Clamp(size ?? 25, 1, MaxSize));
}
