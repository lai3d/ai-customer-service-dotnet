using System.Text.Json;
using CustomerService.Tools;

namespace CustomerService.Tests;

public class ToolsTests
{
    static JsonElement Args(object o) => JsonSerializer.SerializeToElement(o);

    [Fact]
    public async Task OrderLookupToleratesCaseAndWhitespace()
    {
        var r = await new OrderLookup().InvokeAsync("c", Args(new { orderNumber = "  ord-10042 " }), CancellationToken.None);
        Assert.Equal("found", r.Outcome);
        Assert.Contains("\"found\":true", r.Content);
        Assert.Contains("SP884213906SG", r.Content);
        // The model reads this. A status of 1 is a code it cannot translate; a status of
        // IN_TRANSIT is an answer. Found live, on the first turn.
        Assert.Contains("\"status\":\"IN_TRANSIT\"", r.Content);
    }

    [Fact]
    public async Task AMissingOrderIsAValueRatherThanAnError()
    {
        var r = await new OrderLookup().InvokeAsync("c", Args(new { orderNumber = "ORD-99999" }), CancellationToken.None);
        Assert.Equal("not_found", r.Outcome);
        Assert.Contains("\"found\":false", r.Content);
        Assert.Contains("mistyped", r.Content);
    }

    [Fact]
    public async Task UnreadableArgumentsBecomeSomethingTheModelCanAnswerWith()
    {
        var r = await new OrderLookup().InvokeAsync("c", Args(new { nothing = 1 }), CancellationToken.None);
        Assert.Equal("bad_arguments", r.Outcome);
        Assert.Contains("Ask the customer", r.Content);
        var t = await new SupportTickets(10).InvokeAsync("c", JsonDocument.Parse("\"not an object\"").RootElement, CancellationToken.None);
        Assert.Equal("bad_arguments", t.Outcome);
    }

    [Fact]
    public async Task AskingTwiceReturnsTheTicketThatAlreadyExists()
    {
        var tickets = new SupportTickets(10);
        var first = await tickets.InvokeAsync("c", Args(new { summary = "Refund for a damaged lamp", category = "returns" }), CancellationToken.None);
        var second = await tickets.InvokeAsync("c", Args(new { summary = "  refund for a DAMAGED lamp " }), CancellationToken.None);
        Assert.Equal("created", first.Outcome);
        Assert.Equal("duplicate_suppressed", second.Outcome);
        Assert.Contains("\"alreadyExisted\":true", second.Content);
        Assert.Single(tickets.For("c"));
    }

    [Fact]
    public async Task TheCapHoldsAgainstDifferentlyWordedRequests()
    {
        var tickets = new SupportTickets(10);
        for (int i = 0; i < 3; i++)
            Assert.Equal("created", (await tickets.InvokeAsync("c", Args(new { summary = $"problem {i}", category = "other" }), CancellationToken.None)).Outcome);
        var fourth = await tickets.InvokeAsync("c", Args(new { summary = "problem four", category = "other" }), CancellationToken.None);
        Assert.Equal("capped", fourth.Outcome);
        Assert.Contains("maximum number", fourth.Content);
        Assert.Equal(3, tickets.For("c").Count);
    }

    /// <summary>Twenty differently worded requests at once must still leave three tickets.</summary>
    [Fact]
    public async Task TheCapHoldsUnderConcurrentCalls()
    {
        var tickets = new SupportTickets(10);
        var results = await Task.WhenAll(Enumerable.Range(0, 20).Select(i =>
            Task.Run(() => tickets.InvokeAsync("c", Args(new { summary = $"wording number {i}", category = "other" }), CancellationToken.None))));
        Assert.Equal(3, results.Count(r => r.Outcome == "created"));
        Assert.Equal(17, results.Count(r => r.Outcome == "capped"));
        Assert.Equal(3, tickets.For("c").Count);
    }

    [Fact]
    public async Task CategoriesOutsideTheListBecomeOther()
    {
        var tickets = new SupportTickets(10);
        var r = await tickets.InvokeAsync("c", Args(new { summary = "s", category = "Billing dispute" }), CancellationToken.None);
        Assert.Contains("\"category\":\"other\"", r.Content);
        var ok = await tickets.InvokeAsync("c", Args(new { summary = "t", category = " Shipping " }), CancellationToken.None);
        Assert.Contains("\"category\":\"shipping\"", ok.Content);
    }

    [Fact]
    public async Task TheTicketTableIsBounded()
    {
        var tickets = new SupportTickets(2);
        for (int i = 0; i < 10; i++)
            await tickets.InvokeAsync($"c{i}", Args(new { summary = "s", category = "other" }), CancellationToken.None);
        Assert.Equal(2, tickets.Tracked);
        Assert.Empty(tickets.For("c0"));
    }

    /// <summary>
    /// Everything a tool writes is read by a model that has never seen the type, so the check
    /// is on the text, field by field. Found live here as an enum written as 1; found in the
    /// Java implementation the same day as a date written as [2026,9,3], which the model read
    /// correctly for months -- the silent version of the same bug. Asserting on the object,
    /// or reading the JSON back through the writer's serializer, would pass both.
    /// </summary>
    [Fact]
    public async Task EveryFieldAToolWritesIsReadableAsText()
    {
        var order = await new OrderLookup().InvokeAsync("c", Args(new { orderNumber = "ORD-10042" }), CancellationToken.None);
        var ticket = await new SupportTickets(10).InvokeAsync("c", Args(new { summary = "Refund for a lamp", category = "returns", orderNumber = "ORD-10045" }), CancellationToken.None);
        foreach (var content in new[] { order.Content, ticket.Content })
        {
            using var doc = JsonDocument.Parse(content);
            foreach (var (path, value) in Leaves(doc.RootElement, "$"))
            {
                Assert.True(value.ValueKind is JsonValueKind.String or JsonValueKind.True or JsonValueKind.False,
                    $"{path} is {value.ValueKind} ({value.GetRawText()}); a model reads names and dates, not codes and arrays");
                if (path.EndsWith("On") || path.EndsWith("Delivery") || path.EndsWith("At"))
                    Assert.Matches(@"^\d{4}-\d{2}-\d{2}(T\d{2}:\d{2}:\d{2}Z)?$", value.GetString());
            }
        }
        Assert.Contains("\"status\":\"IN_TRANSIT\"", order.Content);
        Assert.Contains("\"orderedOn\":\"2026-08-27\"", order.Content);
        Assert.Contains("\"category\":\"returns\"", ticket.Content);
    }

    static IEnumerable<(string path, JsonElement value)> Leaves(JsonElement e, string path)
    {
        switch (e.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var p in e.EnumerateObject())
                    foreach (var leaf in Leaves(p.Value, $"{path}.{p.Name}")) yield return leaf;
                break;
            case JsonValueKind.Array:
                yield return (path, e);
                break;
            default:
                yield return (path, e);
                break;
        }
    }

    /// <summary>
    /// Descriptions are prompt, not documentation: a rename or a dropped description changes
    /// model behaviour without changing anything else a test would notice.
    /// </summary>
    [Fact]
    public void ToolDefinitionsSayWhatTheToolIsNotFor()
    {
        var order = new OrderLookup().Definition;
        var ticket = new SupportTickets(10).Definition;
        Assert.Equal("lookup_order_status", order.Name);
        Assert.Equal("create_support_ticket", ticket.Name);
        Assert.Contains("Does not modify the order", order.Description);
        Assert.Contains("Do not use it to answer questions that documentation already covers", ticket.Description);
        foreach (var def in new[] { order, ticket })
        {
            Assert.Equal("object", def.Schema.GetProperty("type").GetString());
            foreach (var p in def.Schema.GetProperty("properties").EnumerateObject())
                Assert.False(string.IsNullOrWhiteSpace(p.Value.GetProperty("description").GetString()), $"{def.Name}.{p.Name} has no description");
        }
        Assert.Equal(["orderNumber"], order.Schema.GetProperty("required").EnumerateArray().Select(x => x.GetString()).ToArray());
        Assert.Equal(["summary", "category"], ticket.Schema.GetProperty("required").EnumerateArray().Select(x => x.GetString()).ToArray());
    }
}
