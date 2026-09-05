using System.Text.Json;
using CustomerService.Admin;
using CustomerService.Tests.Support;
using CustomerService.Tickets;
using CustomerService.Tools;

namespace CustomerService.Tests;

/// <summary>Tickets in Postgres: the tool's creation path and the human workflow on top of it.</summary>
[Collection("postgres-8")]
public class TicketStoreTests(Postgres8 pg)
{
    TicketStore Store() => new(pg.Db);
    static readonly Actor Alice = new("alice", StaffRole.Support);
    static readonly Actor Bob = new("bob", StaffRole.Support);
    static readonly Actor Root = new("root", StaffRole.Admin);
    static string NewId() => Guid.NewGuid().ToString();
    static JsonElement Args(object o) => JsonSerializer.SerializeToElement(o);

    [Fact]
    public async Task AskingTwiceReturnsTheTicketThatAlreadyExists()
    {
        var tool = new SupportTickets(Store());
        var id = NewId();
        var first = await tool.InvokeAsync(id, Args(new { summary = "Refund for a damaged lamp", category = "returns" }), CancellationToken.None);
        var second = await tool.InvokeAsync(id, Args(new { summary = "  refund for a DAMAGED lamp ", category = "other" }), CancellationToken.None);
        Assert.Equal("created", first.Outcome);
        Assert.Equal("duplicate_suppressed", second.Outcome);
        Assert.Contains("\"alreadyExisted\":true", second.Content);
        Assert.Contains("\"category\":\"returns\"", first.Content);
        Assert.Single(await Store().ForConversationAsync(id, CancellationToken.None));
    }

    [Fact]
    public async Task TheCapHoldsAgainstDifferentlyWordedRequests()
    {
        var tool = new SupportTickets(Store());
        var id = NewId();
        for (int i = 0; i < 3; i++)
            Assert.Equal("created", (await tool.InvokeAsync(id, Args(new { summary = $"problem {i}", category = "other" }), CancellationToken.None)).Outcome);
        var fourth = await tool.InvokeAsync(id, Args(new { summary = "problem four", category = "other" }), CancellationToken.None);
        Assert.Equal("capped", fourth.Outcome);
        Assert.Contains("maximum number", fourth.Content);
    }

    /// <summary>
    /// Twenty differently worded requests at once, through the database, must leave three
    /// tickets: the guard row locked in the creating transaction is what makes the count and
    /// the insert one step. Without it this is seventeen tickets.
    /// </summary>
    [Fact]
    public async Task TheCapHoldsUnderConcurrentCalls()
    {
        var tool = new SupportTickets(Store());
        var id = NewId();
        var results = await Task.WhenAll(Enumerable.Range(0, 20).Select(i =>
            Task.Run(() => tool.InvokeAsync(id, Args(new { summary = $"wording number {i}", category = "other" }), CancellationToken.None))));
        Assert.Equal(3, results.Count(r => r.Outcome == "created"));
        Assert.Equal(17, results.Count(r => r.Outcome == "capped"));
        Assert.Equal(3, (await Store().ForConversationAsync(id, CancellationToken.None)).Count);
    }

    [Fact]
    public async Task CategoriesOutsideTheListBecomeOther()
    {
        var tool = new SupportTickets(Store());
        var r = await tool.InvokeAsync(NewId(), Args(new { summary = "s", category = "Billing dispute" }), CancellationToken.None);
        Assert.Contains("\"category\":\"other\"", r.Content);
        var ok = await tool.InvokeAsync(NewId(), Args(new { summary = "t", category = " Shipping " }), CancellationToken.None);
        Assert.Contains("\"category\":\"shipping\"", ok.Content);
    }

    [Fact]
    public async Task UnreadableTicketArgumentsBecomeARefusal()
    {
        var tool = new SupportTickets(Store());
        var t = await tool.InvokeAsync(NewId(), JsonDocument.Parse("\"not an object\"").RootElement, CancellationToken.None);
        Assert.Equal("bad_arguments", t.Outcome);
        var blank = await tool.InvokeAsync(NewId(), Args(new { summary = "   ", category = "other" }), CancellationToken.None);
        Assert.Equal("bad_arguments", blank.Outcome);
    }

    // ---- the workflow --------------------------------------------------------------------

    async Task<TicketRecord> Create(TicketStore store, string? conversationId = null)
    {
        var c = await store.CreateAsync(conversationId ?? NewId(), "Customer wants a refund for a broken lamp", "returns", "ORD-10045", CancellationToken.None);
        Assert.True(c.Created);
        return c.Ticket;
    }

    [Fact]
    public async Task TheWholeLoopIsTraceableInTheHistory()
    {
        var store = Store();
        var t = await Create(store);
        var claimed = await store.ClaimAsync(t.TicketNumber, 1, Alice, CancellationToken.None);
        Assert.Equal((TicketState.Claimed, "alice", 2), (claimed.Ticket.State, claimed.Ticket.Owner, claimed.Ticket.Version));
        var noted = await store.NoteAsync(t.TicketNumber, 2, "Called the customer.", Alice, CancellationToken.None);
        var resolved = await store.ResolveAsync(t.TicketNumber, 3, "Refund issued to the original card.", Alice, CancellationToken.None);
        var closed = await store.CloseAsync(t.TicketNumber, 4, null, Alice, CancellationToken.None);
        Assert.Equal(TicketState.Closed, closed.Ticket.State);
        Assert.Equal(["created", "claimed", "note", "resolved", "closed"], closed.History.Select(e => e.Kind).ToArray());
        Assert.Equal(["assistant", "alice", "alice", "alice", "alice"], closed.History.Select(e => e.Actor).ToArray());
        // The conclusion lives on the resolving event, never on the row.
        Assert.Equal("Refund issued to the original card.", closed.History[3].Note);
        Assert.Equal([1, 2, 3, 4, 5], closed.History.Select(e => e.VersionAfter).ToArray());
        Assert.NotNull(noted);
    }

    [Fact]
    public async Task AStaleVersionIsAConflictAndWritesNothing()
    {
        var store = Store();
        var t = await Create(store);
        await store.ClaimAsync(t.TicketNumber, 1, Alice, CancellationToken.None);
        // Bob's page still shows version 1.
        await Assert.ThrowsAsync<TicketConflictException>(() => store.ClaimAsync(t.TicketNumber, 1, Bob, CancellationToken.None));
        var d = await store.GetAsync(t.TicketNumber, CancellationToken.None);
        Assert.Equal(("alice", 2, 2), (d!.Ticket.Owner, d.Ticket.Version, d.History.Count));
    }

    [Fact]
    public async Task ClaimingIsFirstComeFirstServed()
    {
        var store = Store();
        var t = await Create(store);
        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(i => Task.Run(async () =>
        {
            try { await store.ClaimAsync(t.TicketNumber, 1, new Actor($"agent{i}", StaffRole.Support), CancellationToken.None); return "claimed"; }
            catch (TicketConflictException) { return "conflict"; }
        })));
        Assert.Equal(1, results.Count(r => r == "claimed"));
        Assert.Equal(7, results.Count(r => r == "conflict"));
    }

    [Fact]
    public async Task OnlyTheOwnerOrAnAdminMayResolveReleaseOrClose()
    {
        var store = Store();
        var t = await Create(store);
        await store.ClaimAsync(t.TicketNumber, 1, Alice, CancellationToken.None);
        await Assert.ThrowsAsync<TicketRuleException>(() => store.ResolveAsync(t.TicketNumber, 2, "done", Bob, CancellationToken.None));
        await Assert.ThrowsAsync<TicketRuleException>(() => store.ReleaseAsync(t.TicketNumber, 2, Bob, CancellationToken.None));
        var byAdmin = await store.ResolveAsync(t.TicketNumber, 2, "Refunded by an admin.", Root, CancellationToken.None);
        Assert.Equal(TicketState.Resolved, byAdmin.Ticket.State);
        Assert.Equal("alice", byAdmin.Ticket.Owner);
    }

    [Fact]
    public async Task ResolvingRequiresAConclusionAndReopeningRequiresAReason()
    {
        var store = Store();
        var t = await Create(store);
        await store.ClaimAsync(t.TicketNumber, 1, Alice, CancellationToken.None);
        await Assert.ThrowsAsync<TicketRuleException>(() => store.ResolveAsync(t.TicketNumber, 2, "  ", Alice, CancellationToken.None));
        await store.ResolveAsync(t.TicketNumber, 2, "Refund issued.", Alice, CancellationToken.None);
        await Assert.ThrowsAsync<TicketRuleException>(() => store.ReopenAsync(t.TicketNumber, 3, null, Bob, CancellationToken.None));
        var reopened = await store.ReopenAsync(t.TicketNumber, 3, "Customer says the refund never arrived.", Bob, CancellationToken.None);
        // A reopened ticket is nobody's until claimed again, and the old conclusion is history, not state.
        Assert.Equal((TicketState.Open, (string?)null), (reopened.Ticket.State, reopened.Ticket.Owner));
        Assert.Equal("reopened", reopened.History[^1].Kind);
        Assert.Equal("Customer says the refund never arrived.", reopened.History[^1].Note);
        Assert.Equal("Refund issued.", reopened.History.Single(e => e.Kind == "resolved").Note);
    }

    [Fact]
    public async Task IllegalTransitionsAreRefusedByRule()
    {
        var store = Store();
        var t = await Create(store);
        await Assert.ThrowsAsync<TicketRuleException>(() => store.ResolveAsync(t.TicketNumber, 1, "x", Alice, CancellationToken.None));
        await Assert.ThrowsAsync<TicketRuleException>(() => store.CloseAsync(t.TicketNumber, 1, null, Alice, CancellationToken.None));
        await Assert.ThrowsAsync<TicketRuleException>(() => store.ReopenAsync(t.TicketNumber, 1, "why", Alice, CancellationToken.None));
        await Assert.ThrowsAsync<TicketRuleException>(() => store.AssignAsync(t.TicketNumber, 1, "bob", Alice, CancellationToken.None));
        var assigned = await store.AssignAsync(t.TicketNumber, 1, "bob", Root, CancellationToken.None);
        Assert.Equal(("bob", TicketState.Claimed), (assigned.Ticket.Owner, assigned.Ticket.State));
        await Assert.ThrowsAsync<TicketNotFoundException>(() => store.ClaimAsync("TKT-1", 1, Alice, CancellationToken.None));
    }

    [Fact]
    public async Task TheQueueFiltersByStateOwnerAndConversation()
    {
        var store = Store();
        var conv = NewId();
        var a = await Create(store, conv);
        var b = await Create(store);
        await store.ClaimAsync(b.TicketNumber, 1, Alice, CancellationToken.None);
        var open = await store.SearchAsync(new TicketFilter(TicketState.Open, null, conv, null, null), 1, 25, CancellationToken.None);
        Assert.Single(open.Items);
        Assert.Equal(a.TicketNumber, open.Items[0].TicketNumber);
        var alices = await store.SearchAsync(new TicketFilter(null, "alice", null, null, null), 1, 25, CancellationToken.None);
        Assert.Contains(alices.Items, t => t.TicketNumber == b.TicketNumber);
        Assert.All(alices.Items, t => Assert.Equal("alice", t.Owner));
    }
}
