using CustomerService.Tests.Support;

namespace CustomerService.Tests;

[Collection("postgres-8")]
public class DatabaseSmokeTests(Postgres8 pg)
{
    [Fact]
    public async Task TheSchemaIsAppliedAndTheVectorTypeIsRegistered()
    {
        await using var cmd = pg.Db.CreateCommand("SELECT count(*) FROM faq_document");
        Assert.Equal(0, Convert.ToInt32(await cmd.ExecuteScalarAsync()));
    }
}
