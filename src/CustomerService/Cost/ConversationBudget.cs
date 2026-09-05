using CustomerService.Support;

namespace CustomerService.Cost;

/// <summary>
/// Caps the tokens one conversation may spend.
///
/// A message window bounds any single request; nothing bounds the number of requests. A
/// customer who keeps typing, or a script that does, runs indefinitely, and the failure is
/// undramatic: no error, no alert, a larger invoice. Reaching the cap is also a good reason
/// to hand the customer to a human -- a conversation that long is not going well.
///
/// Spend is held in a bounded LRU map, per replica, reset on restart. That is honest about
/// what it is: blast-radius limiting, not a ledger. Redis or Postgres would be the real
/// thing.
/// </summary>
public sealed class ConversationBudget(long limit, int maxTracked)
{
    readonly Lock gate = new();
    readonly BoundedLru<string, Spend> spend = new(maxTracked);

    sealed class Spend { public long Tokens; }

    public long Limit => limit;

    /// <summary>Throws <see cref="BudgetExceededException"/> when the conversation is over budget.</summary>
    public void Check(string conversationId)
    {
        if (limit <= 0) return;
        lock (gate)
        {
            if (spend.TryGet(conversationId, out var s) && s.Tokens >= limit)
                throw new BudgetExceededException(conversationId, s.Tokens, limit);
        }
    }

    /// <summary>Adds a turn's tokens to a conversation's running total.</summary>
    public long Record(string conversationId, long tokens)
    {
        lock (gate)
        {
            var s = spend.GetOrAdd(conversationId, () => new Spend());
            s.Tokens += tokens;
            return s.Tokens;
        }
    }

    public long Spent(string conversationId)
    {
        lock (gate) return spend.TryGet(conversationId, out var s) ? s.Tokens : 0;
    }

    /// <summary>How many conversations are tracked. For tests: it must stay bounded.</summary>
    public int Tracked { get { lock (gate) return spend.Count; } }
}

/// <summary>
/// A distinct exception because the right response is a 429 pointing at a human, not a 500.
/// </summary>
public sealed class BudgetExceededException(string conversationId, long spent, long limit)
    : Exception($"conversation has spent {spent} tokens against a budget of {limit}")
{
    public string ConversationId => conversationId;
    public long Spent => spent;
    public long Limit => limit;
}
