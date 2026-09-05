using CustomerService.Chat;
using CustomerService.Rag;
using CustomerService.Tests.Support;

namespace CustomerService.Tests;

public class CorpusTests
{
    [Fact]
    public void EveryLanguageOfEveryEntryIsIndexed()
    {
        var corpus = Corpus.Load(Repo.CorpusPath);
        var docs = corpus.Documents();
        Assert.Equal(18, corpus.Entries.Count);
        Assert.Equal(36, docs.Count);
        Assert.Equal(36, docs.Select(d => d.Id).Distinct().Count());
        Assert.All(corpus.Entries, e => Assert.Equal(["en", "zh"], e.Localized.Select(l => l.Language).Order().ToArray()));
        Assert.All(docs, d => Assert.StartsWith("Q: ", d.Content));
        Assert.Equal("faq:returns-window:en", docs[0].Id);
    }

    /// <summary>The corpus is shared with the Java and Go implementations byte for byte.</summary>
    [Fact]
    public void TheCorpusIsByteIdenticalToTheSiblingImplementations()
    {
        var hash = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(Repo.CorpusPath)));
        Assert.Equal("f3a06e0788e372577958ac0cd2ae245d8147f9aedfcfa0e29845fbeb0c90d4d4", hash);
    }
}

public class ConversationLocksTests
{
    [Fact]
    public async Task OverlappingAcquisitionsOnOneConversationSerialise()
    {
        var locks = new ConversationLocks();
        using var first = await locks.AcquireAsync("c", CancellationToken.None);
        var second = locks.AcquireAsync("c", CancellationToken.None);
        await Task.Delay(50);
        Assert.False(second.IsCompleted);
        using var other = await locks.AcquireAsync("d", CancellationToken.None);
        first.Dispose();
        (await second).Dispose();
        Assert.Equal(1, locks.InFlight);
    }

    [Fact]
    public async Task AWaitingTurnGivesUpWhenItsRequestIsCancelled()
    {
        var locks = new ConversationLocks();
        using var held = await locks.AcquireAsync("c", CancellationToken.None);
        using var cts = new CancellationTokenSource(50);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => locks.AcquireAsync("c", cts.Token));
        Assert.Equal(1, locks.InFlight);
    }

    [Fact]
    public async Task TheConversationLockTableEmpties()
    {
        var locks = new ConversationLocks();
        await Task.WhenAll(Enumerable.Range(0, 50).Select(async i =>
        {
            using var l = await locks.AcquireAsync($"c{i % 5}", CancellationToken.None);
            await Task.Yield();
        }));
        Assert.Equal(0, locks.InFlight);
    }
}
