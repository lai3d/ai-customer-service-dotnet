namespace CustomerService.Chat;

/// <summary>
/// Serialises whole turns per conversation. A turn reads history, calls the model and writes
/// a reply, and those steps are only coherent together. Without this, two overlapping
/// requests on one conversation interleave: the second one's user message and reply land
/// between the first one's write and its history read, so the first sends the model a
/// conversation ending in somebody else's answer -- and because passages are attached only to
/// a trailing user message, its retrieved material is dropped at the same time. Two browser
/// tabs are enough to cause it.
///
/// The table is bounded by the number of conversations with a request in flight, not by the
/// number ever seen: entries are reference-counted and removed when the last holder leaves.
/// Waiting is cancellable so a caller whose client has already gone away stops waiting
/// instead of holding a place in the queue.
///
/// Single process only. Two replicas mean two lock tables, and a conversation load-balanced
/// across both can still interleave. Postgres advisory locks would be the real thing.
/// </summary>
public sealed class ConversationLocks
{
    readonly Lock gate = new();
    readonly Dictionary<string, Holder> holders = new();

    sealed class Holder
    {
        public readonly SemaphoreSlim Slot = new(1, 1);
        public int Refs;
    }

    /// <summary>Blocks until the conversation is free or the token is cancelled. Dispose the result exactly once.</summary>
    public async Task<IDisposable> AcquireAsync(string conversationId, CancellationToken ct)
    {
        Holder holder;
        lock (gate)
        {
            if (!holders.TryGetValue(conversationId, out holder!))
            {
                holder = new Holder();
                holders[conversationId] = holder;
            }
            holder.Refs++;
        }
        try
        {
            await holder.Slot.WaitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            Release(conversationId, holder, held: false);
            throw;
        }
        return new Releaser(() => Release(conversationId, holder, held: true));
    }

    void Release(string conversationId, Holder holder, bool held)
    {
        if (held) holder.Slot.Release();
        lock (gate)
        {
            holder.Refs--;
            if (holder.Refs == 0) holders.Remove(conversationId);
        }
    }

    /// <summary>Conversations currently holding or waiting for a lock. For tests: it must return to zero.</summary>
    public int InFlight { get { lock (gate) return holders.Count; } }

    sealed class Releaser(Action release) : IDisposable
    {
        int done;
        public void Dispose() { if (Interlocked.Exchange(ref done, 1) == 0) release(); }
    }
}
