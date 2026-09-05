using CustomerService.Cost;

namespace CustomerService.Tests;

public class CostTests
{
    [Fact]
    public void AConversationIsRefusedOnceItHasSpentItsBudget()
    {
        var budget = new ConversationBudget(1000, 100);
        budget.Check("c1");
        budget.Record("c1", 999);
        budget.Check("c1");
        budget.Record("c1", 1);
        var ex = Assert.Throws<BudgetExceededException>(() => budget.Check("c1"));
        Assert.Equal(1000, ex.Spent);
        budget.Check("c2");
    }

    [Fact]
    public void AZeroBudgetDisablesTheCapButStillTracksSpend()
    {
        var budget = new ConversationBudget(0, 100);
        budget.Record("c1", 5_000_000);
        budget.Check("c1");
        Assert.Equal(5_000_000, budget.Spent("c1"));
    }

    [Fact]
    public void SpendTrackingIsBounded()
    {
        var budget = new ConversationBudget(1000, 3);
        for (int i = 0; i < 50; i++) budget.Record($"c{i}", 1);
        Assert.Equal(3, budget.Tracked);
        Assert.Equal(0, budget.Spent("c0"));
        Assert.Equal(1, budget.Spent("c49"));
    }

    [Fact]
    public void AnUnpricedModelCountsTokensWithoutInventingACost()
    {
        var (usd, priced) = Prices.Usd("gpt-5-2025-08-07", 1000, 1000);
        Assert.False(priced);
        Assert.Equal(0, usd);
        var (opus, ok) = Prices.Usd("claude-opus-5", 1_000_000, 1_000_000);
        Assert.True(ok);
        Assert.Equal(30.0, opus, 6);
    }
}
