using CustomerService.Tests.Support;

namespace CustomerService.Tests;

[Collection("postgres-8")]
public class DatabaseSmokeTests(Postgres8 pg)
{
    /// <summary>
    /// The schema is applied and the vector extension is present. Not an assertion on row
    /// counts: the collection's database is shared with tests that ingest, and the first CI
    /// run on a different machine ran them in a different order -- 0 expected, 36 found. A
    /// test that depends on which test ran before it is testing the scheduler.
    /// </summary>
    [Fact]
    public async Task TheSchemaIsAppliedAndTheVectorExtensionIsPresent()
    {
        await using var ext = pg.Db.CreateCommand("SELECT count(*) FROM pg_extension WHERE extname = 'vector'");
        Assert.Equal(1, Convert.ToInt32(await ext.ExecuteScalarAsync()));
        await using var cols = pg.Db.CreateCommand(
            "SELECT format_type(atttypid, atttypmod) FROM pg_attribute WHERE attrelid = 'faq_document'::regclass AND attname = 'embedding'");
        Assert.Equal($"vector({pg.Dimensions})", await cols.ExecuteScalarAsync());
        await using var memory = pg.Db.CreateCommand("SELECT count(*) FROM chat_memory WHERE conversation_id = 'no-such-conversation'");
        Assert.Equal(0, Convert.ToInt32(await memory.ExecuteScalarAsync()));
    }
}
