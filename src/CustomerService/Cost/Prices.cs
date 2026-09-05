namespace CustomerService.Cost;

/// <summary>Dollars per million tokens. Keep in step with the provider's published pricing.</summary>
public readonly record struct Price(double InputPerMillionUsd, double OutputPerMillionUsd);

public static class Prices
{
    /// <summary>
    /// Keyed on the model id the provider <em>reports</em>, not the one requested. Asking for
    /// "gpt-5" yields "gpt-5-2025-08-07", and a price keyed on "gpt-5" silently never
    /// matches: tokens keep counting while cost stays at zero. A model with no entry has
    /// its tokens counted but not costed, and <c>chat_unpriced_model_calls_total</c> counts
    /// the misses so a flat cost meter is visible rather than plausible.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, Price> Table = new Dictionary<string, Price>
    {
        ["claude-opus-5"] = new(5.00, 25.00),
        ["claude-sonnet-5"] = new(2.00, 10.00),
        ["claude-haiku-4-5"] = new(1.00, 5.00),
    };

    /// <summary>The cost of a call, and whether the model had a price at all.</summary>
    public static (double usd, bool priced) Usd(string model, long inputTokens, long outputTokens)
    {
        if (!Table.TryGetValue(model, out var p)) return (0, false);
        return (inputTokens / 1e6 * p.InputPerMillionUsd + outputTokens / 1e6 * p.OutputPerMillionUsd, true);
    }
}
