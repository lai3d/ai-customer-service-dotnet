using CustomerService.Llm;
using Npgsql;

namespace CustomerService.Chat;

/// <summary>
/// The conversation history, in Postgres alongside the vectors. It stores what the customer
/// actually wrote and what the assistant actually replied -- never the passages retrieval
/// found. That distinction is the ordering constraint the Java implementation had to pin
/// with a test: there, retrieval rewrote the user message to carry the passages and memory
/// stored whatever message it was handed. Here, memory is written before retrieval runs and
/// passages are attached to the outgoing request instead.
/// </summary>
public sealed class ConversationMemory(NpgsqlDataSource db, int window)
{
    public async Task AppendAsync(string conversationId, Role role, string content, CancellationToken ct)
    {
        if (content.Length == 0) return;
        await using var cmd = db.CreateCommand("INSERT INTO chat_memory (conversation_id, role, content) VALUES ($1, $2, $3)");
        cmd.Parameters.Add(new NpgsqlParameter { Value = conversationId });
        cmd.Parameters.Add(new NpgsqlParameter { Value = role == Role.User ? "user" : "assistant" });
        cmd.Parameters.Add(new NpgsqlParameter { Value = content });
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>
    /// The last <c>window</c> messages, oldest first. Every message is re-sent and re-billed
    /// on every turn, so the window is a cost and latency lever rather than a memory setting.
    /// Consecutive messages that share a role are merged: a turn whose model call fails after
    /// the user message is stored leaves no assistant reply behind, so the next turn's
    /// history has two user messages in a row, and providers differ on whether that is
    /// accepted. Merging loses nothing -- the two messages were consecutive for the customer too.
    /// </summary>
    public async Task<List<ModelMessage>> HistoryAsync(string conversationId, CancellationToken ct)
    {
        await using var cmd = db.CreateCommand("""
            SELECT role, content FROM (
                SELECT id, role, content FROM chat_memory
                WHERE conversation_id = $1
                ORDER BY id DESC
                LIMIT $2
            ) recent
            ORDER BY id ASC
            """);
        cmd.Parameters.Add(new NpgsqlParameter { Value = conversationId });
        cmd.Parameters.Add(new NpgsqlParameter { Value = window });
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        var history = new List<ModelMessage>();
        while (await reader.ReadAsync(ct))
        {
            var role = reader.GetString(0) == "user" ? Role.User : Role.Assistant;
            var text = reader.GetString(1);
            if (history.Count > 0 && history[^1].Role == role)
                history[^1] = history[^1] with { Text = history[^1].Text + "\n\n" + text };
            else
                history.Add(new ModelMessage(role, text));
        }
        return history;
    }

    public async Task<int> CountAsync(string conversationId, CancellationToken ct)
    {
        await using var cmd = db.CreateCommand("SELECT count(*) FROM chat_memory WHERE conversation_id = $1");
        cmd.Parameters.Add(new NpgsqlParameter { Value = conversationId });
        return Convert.ToInt32(await cmd.ExecuteScalarAsync(ct));
    }
}
