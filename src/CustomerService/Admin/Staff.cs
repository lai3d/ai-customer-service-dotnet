using System.Security.Cryptography;
using System.Text;
using Npgsql;

namespace CustomerService.Admin;

public enum StaffRole { Admin, Support }

public static class StaffRoles
{
    public static string Name(this StaffRole r) => r == StaffRole.Admin ? "admin" : "support";
    public static bool TryParse(string? s, out StaffRole role)
    {
        role = StaffRole.Support;
        switch (s?.Trim().ToLowerInvariant())
        {
            case "admin": role = StaffRole.Admin; return true;
            case "support": role = StaffRole.Support; return true;
            default: return false;
        }
    }
}

public sealed record StaffAccount(string Username, StaffRole Role, bool Enabled, DateTimeOffset CreatedAt);

/// <summary>The signed-in operator, as every admin request sees it.</summary>
public sealed record Actor(string Username, StaffRole Role)
{
    public bool IsAdmin => Role == StaffRole.Admin;
}

public sealed class DuplicateStaffAccountException(string username) : Exception($"a staff account named {username} already exists");

/// <summary>
/// Staff accounts in Postgres. Passwords are PBKDF2-SHA256 with a per-account salt, through
/// the framework's own implementation; no password ever reaches a log.
/// </summary>
public sealed class StaffAccounts(NpgsqlDataSource db)
{
    const int Iterations = 210_000;

    public static string Hash(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        var key = Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, HashAlgorithmName.SHA256, 32);
        return $"pbkdf2-sha256${Iterations}${Convert.ToBase64String(salt)}${Convert.ToBase64String(key)}";
    }

    public static bool Verify(string password, string stored)
    {
        var parts = stored.Split('$');
        if (parts.Length != 4 || parts[0] != "pbkdf2-sha256") return false;
        var salt = Convert.FromBase64String(parts[2]);
        var expected = Convert.FromBase64String(parts[3]);
        var actual = Rfc2898DeriveBytes.Pbkdf2(password, salt, int.Parse(parts[1]), HashAlgorithmName.SHA256, expected.Length);
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }

    public static bool ValidUsername(string? u) =>
        u is { Length: >= 2 and <= 64 } && u.All(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c is '.' or '_' or '-');

    public async Task CreateAsync(string username, string password, StaffRole role, CancellationToken ct)
    {
        if (!ValidUsername(username)) throw new ArgumentException("username must be 2-64 characters of a-z 0-9 . _ -");
        if (password.Length < 12) throw new ArgumentException("password must be at least 12 characters");
        await using var cmd = db.CreateCommand(
            "INSERT INTO staff_account (username, password_hash, role, enabled, created_at) VALUES ($1, $2, $3, true, now())");
        cmd.Parameters.Add(new NpgsqlParameter { Value = username });
        cmd.Parameters.Add(new NpgsqlParameter { Value = Hash(password) });
        cmd.Parameters.Add(new NpgsqlParameter { Value = role.Name() });
        try { await cmd.ExecuteNonQueryAsync(ct); }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation) { throw new DuplicateStaffAccountException(username); }
    }

    /// <summary>
    /// Seeds the first admin, only when the table is empty. Never overwrites or resets an
    /// account, so the variables are safe to leave set.
    /// </summary>
    public async Task<bool> SeedAsync(string username, string password, CancellationToken ct)
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        await using (var lockCmd = new NpgsqlCommand("LOCK TABLE staff_account IN EXCLUSIVE MODE", conn, tx))
            await lockCmd.ExecuteNonQueryAsync(ct);
        await using (var count = new NpgsqlCommand("SELECT count(*) FROM staff_account", conn, tx))
            if (Convert.ToInt64(await count.ExecuteScalarAsync(ct)) > 0) { await tx.RollbackAsync(ct); return false; }
        await using (var ins = new NpgsqlCommand(
            "INSERT INTO staff_account (username, password_hash, role, enabled, created_at) VALUES ($1, $2, 'admin', true, now())", conn, tx))
        {
            ins.Parameters.Add(new NpgsqlParameter { Value = username });
            ins.Parameters.Add(new NpgsqlParameter { Value = Hash(password) });
            await ins.ExecuteNonQueryAsync(ct);
        }
        await tx.CommitAsync(ct);
        return true;
    }

    public async Task<IReadOnlyList<StaffAccount>> ListAsync(CancellationToken ct)
    {
        await using var cmd = db.CreateCommand("SELECT username, role, enabled, created_at FROM staff_account ORDER BY created_at, username");
        await using var r = await cmd.ExecuteReaderAsync(ct);
        var list = new List<StaffAccount>();
        while (await r.ReadAsync(ct))
            list.Add(new StaffAccount(r.GetString(0), r.GetString(1) == "admin" ? StaffRole.Admin : StaffRole.Support, r.GetBoolean(2), r.GetFieldValue<DateTimeOffset>(3)));
        return list;
    }

    /// <summary>The account and its hash, for login. Null when there is no such account.</summary>
    public async Task<(StaffAccount account, string hash)?> FindAsync(string username, CancellationToken ct)
    {
        await using var cmd = db.CreateCommand("SELECT username, role, enabled, created_at, password_hash FROM staff_account WHERE username = $1");
        cmd.Parameters.Add(new NpgsqlParameter { Value = username });
        await using var r = await cmd.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct)) return null;
        return (new StaffAccount(r.GetString(0), r.GetString(1) == "admin" ? StaffRole.Admin : StaffRole.Support, r.GetBoolean(2), r.GetFieldValue<DateTimeOffset>(3)), r.GetString(4));
    }

    /// <summary>Changes role, enabled flag or password. Disabling also ends the account's sessions.</summary>
    public async Task<bool> UpdateAsync(string username, StaffRole? role, bool? enabled, string? password, CancellationToken ct)
    {
        if (password is { Length: < 12 }) throw new ArgumentException("password must be at least 12 characters");
        await using var conn = await db.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        await using var cmd = new NpgsqlCommand(
            "UPDATE staff_account SET role = COALESCE($2, role), enabled = COALESCE($3, enabled), password_hash = COALESCE($4, password_hash) WHERE username = $1", conn, tx);
        cmd.Parameters.Add(new NpgsqlParameter { Value = username });
        cmd.Parameters.Add(new NpgsqlParameter { Value = (object?)role?.Name() ?? DBNull.Value, NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Varchar });
        cmd.Parameters.Add(new NpgsqlParameter { Value = (object?)enabled ?? DBNull.Value, NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Boolean });
        cmd.Parameters.Add(new NpgsqlParameter { Value = password is null ? DBNull.Value : Hash(password), NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Text });
        var n = await cmd.ExecuteNonQueryAsync(ct);
        if (n > 0 && (enabled == false || password is not null))
        {
            // A disabled account, or one whose password changed, keeps no sessions.
            await using var del = new NpgsqlCommand("DELETE FROM staff_session WHERE username = $1", conn, tx);
            del.Parameters.Add(new NpgsqlParameter { Value = username });
            await del.ExecuteNonQueryAsync(ct);
        }
        await tx.CommitAsync(ct);
        return n > 0;
    }
}

/// <summary>
/// Bearer sessions. The token the browser holds is random; the table holds its SHA-256, so a
/// dump of the table signs nobody in. Idle expiry is a sliding window enforced on every read.
/// </summary>
public sealed class StaffSessions(NpgsqlDataSource db, StaffAccounts accounts, TimeSpan idleTimeout, Func<DateTimeOffset>? clock = null)
{
    readonly Func<DateTimeOffset> now = clock ?? (() => DateTimeOffset.UtcNow);

    public sealed record Session(string Token, Actor Actor, DateTimeOffset ExpiresAt);

    static string HashToken(string token) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    /// <summary>Null on a wrong username, a wrong password or a disabled account -- indistinguishably.</summary>
    public async Task<Session?> LoginAsync(string username, string password, CancellationToken ct)
    {
        var found = await accounts.FindAsync(username, ct);
        // Verify against a real hash even when the account does not exist, so the time a
        // rejection takes does not say which usernames exist.
        var hash = found?.hash ?? DummyHash;
        var ok = StaffAccounts.Verify(password, hash) && found is { account.Enabled: true };
        if (!ok) return null;
        var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var at = now();
        await using var cmd = db.CreateCommand("INSERT INTO staff_session (token_hash, username, created_at, last_seen_at) VALUES ($1, $2, $3, $3)");
        cmd.Parameters.Add(new NpgsqlParameter { Value = HashToken(token) });
        cmd.Parameters.Add(new NpgsqlParameter { Value = username });
        cmd.Parameters.Add(new NpgsqlParameter { Value = at });
        await cmd.ExecuteNonQueryAsync(ct);
        return new Session(token, new Actor(found!.Value.account.Username, found.Value.account.Role), at + idleTimeout);
    }

    static readonly string DummyHash = StaffAccounts.Hash("not-a-real-password-just-timing");

    /// <summary>The actor behind a token, or null when it is unknown, expired, or its account is disabled.</summary>
    public async Task<Actor?> ResolveAsync(string token, CancellationToken ct)
    {
        var at = now();
        await using var cmd = db.CreateCommand("""
            UPDATE staff_session s SET last_seen_at = $2
            FROM staff_account a
            WHERE s.token_hash = $1 AND s.username = a.username AND a.enabled AND s.last_seen_at > $3
            RETURNING a.username, a.role
            """);
        cmd.Parameters.Add(new NpgsqlParameter { Value = HashToken(token) });
        cmd.Parameters.Add(new NpgsqlParameter { Value = at });
        cmd.Parameters.Add(new NpgsqlParameter { Value = at - idleTimeout });
        await using var r = await cmd.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct)) return null;
        return new Actor(r.GetString(0), r.GetString(1) == "admin" ? StaffRole.Admin : StaffRole.Support);
    }

    public async Task LogoutAsync(string token, CancellationToken ct)
    {
        await using var cmd = db.CreateCommand("DELETE FROM staff_session WHERE token_hash = $1");
        cmd.Parameters.Add(new NpgsqlParameter { Value = HashToken(token) });
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
