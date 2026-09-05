using System.Text.Json;
using CustomerService.Llm;

namespace CustomerService.Tools;

public enum OrderStatus { PREPARING, IN_TRANSIT, DELIVERED, RETURN_IN_PROGRESS, CANCELLED }

public sealed record Order(
    string OrderNumber, OrderStatus Status, string OrderedOn, string? EstimatedDelivery,
    string? Carrier, string? TrackingNumber, string Items);

/// <summary>Reads one order by number. A mock: what matters is the calling contract.</summary>
public sealed class OrderLookup : ITool
{
    // Fixed data, deliberately: it makes the model's behaviour -- when it decides to look an
    // order up and what it does with the answer -- the only variable. The dates match the
    // Java and Go implementations' so a conversation can be replayed against any of them.
    static readonly Dictionary<string, Order> Orders = new()
    {
        ["ORD-10042"] = new("ORD-10042", OrderStatus.IN_TRANSIT, "2026-08-27", "2026-09-03", "SingPost", "SP884213906SG", "1 x Noise-cancelling headphones"),
        ["ORD-10043"] = new("ORD-10043", OrderStatus.PREPARING, "2026-08-31", "2026-09-05", null, null, "2 x Cotton t-shirt (M, navy)"),
        ["ORD-10044"] = new("ORD-10044", OrderStatus.DELIVERED, "2026-08-18", "2026-08-22", "DHL", "JD0002088776", "1 x Espresso machine"),
        ["ORD-10045"] = new("ORD-10045", OrderStatus.RETURN_IN_PROGRESS, "2026-08-09", "2026-08-14", "DHL", "JD0002071140", "1 x Desk lamp"),
        ["ORD-10046"] = new("ORD-10046", OrderStatus.CANCELLED, "2026-08-29", null, null, null, "1 x Mechanical keyboard"),
    };

    public ToolDefinition Definition { get; } = new(
        "lookup_order_status",
        "Look up the current delivery status of one order by its order number. Use this whenever a " +
        "customer asks where their order is, when it will arrive, or whether it has shipped. Returns " +
        "the status, estimated delivery date, and carrier tracking details when they exist. Does not " +
        "modify the order. If the order number cannot be found the result says so, which means the " +
        "customer should be asked to check it rather than told the order does not exist.",
        ToolJson.Schema(new Dictionary<string, (string, string)>
        {
            ["orderNumber"] = ("string", "The order number, for example ORD-10042"),
        }, "orderNumber"));

    // A value with found:false rather than an error. Whatever a tool layer does with a
    // thrown exception, the model ends up seeing something written for developers -- and it
    // has nothing to reason about. found:false with a plain explanation lets the assistant
    // say "I can't find that order number, could you check it?" instead.
    sealed record LookupResult(bool Found, Order? Order = null, string? Explanation = null);

    public Task<ToolOutcome> InvokeAsync(string conversationId, JsonElement arguments, CancellationToken ct)
    {
        var raw = ToolJson.OptionalString(arguments, "orderNumber");
        if (raw is null)
            return Task.FromResult(new ToolOutcome(ToolJson.Serialize(new LookupResult(false,
                Explanation: "The order number argument could not be read. Ask the customer to repeat it.")), "bad_arguments"));

        // Customers paste order numbers out of emails, so a model relaying "ord-10042 "
        // should not be told the order does not exist.
        var key = raw.Trim().ToUpperInvariant();
        if (!Orders.TryGetValue(key, out var order))
            return Task.FromResult(new ToolOutcome(ToolJson.Serialize(new LookupResult(false,
                Explanation: "No order matches that number. It may have been mistyped, or it may belong to a different account.")), "not_found"));
        return Task.FromResult(new ToolOutcome(ToolJson.Serialize(new LookupResult(true, order)), "found"));
    }
}
