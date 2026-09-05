using Npgsql;
using Pgvector;

namespace CustomerService.Rag;

public sealed record SearchOptions(int TopK, double Threshold, string Language = "");

/// <summary>Holds the corpus vectors, in the business database alongside the conversations.</summary>
public sealed class VectorStore(NpgsqlDataSource db)
{
    /// <summary>
    /// Writes the corpus, discarding whatever was there before, in one transaction. Appending
    /// instead is the obvious bug and it is not merely wasteful: duplicates crowd out
    /// distinct passages inside the top-k window, so the model sees one answer four times
    /// instead of four different ones.
    ///
    /// TRUNCATE, not DELETE, and that is a measurement. Every restart re-ingests, and a DELETE
    /// leaves the old rows in the HNSW index as dead entries until VACUUM removes them. An HNSW
    /// scan is approximate: it collects hnsw.ef_search candidates from the graph and only then
    /// drops the ones whose heap tuples are dead, so once enough restarts have piled up the
    /// candidates are mostly dead and ORDER BY ... LIMIT 8 returns fewer than eight live rows.
    /// Reproduced in psql against pgvector 0.8.6 with autovacuum off: thirty delete-and-
    /// reinsert transactions of the same 36 rows, then the index scan returns 0 rows, a
    /// sequential scan 8, and after VACUUM the index scan 8 again. The test suite met it as a
    /// turn whose retrieval evidence was "[]" about one run in four, because every test
    /// re-ingests. TRUNCATE rebuilds the index empty; readers wait on its lock until the new
    /// rows are committed, which is the right trade for a 36-document load at startup.
    /// ReingestingThirtyTimesStillRetrievesEverything pins it, with autovacuum off.
    /// </summary>
    public async Task ReplaceAsync(IReadOnlyList<Document> docs, IReadOnlyList<float[]> vectors, CancellationToken ct)
    {
        if (docs.Count != vectors.Count)
            throw new ArgumentException($"have {docs.Count} documents and {vectors.Count} vectors");
        await using var conn = await db.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        await using (var del = new NpgsqlCommand("TRUNCATE faq_document", conn, tx))
            await del.ExecuteNonQueryAsync(ct);
        for (int i = 0; i < docs.Count; i++)
        {
            var d = docs[i];
            await using var cmd = new NpgsqlCommand(
                "INSERT INTO faq_document (id, entry_id, language, category, question, answer, content, embedding) " +
                "VALUES ($1, $2, $3, $4, $5, $6, $7, $8)", conn, tx);
            cmd.Parameters.Add(new NpgsqlParameter { Value = d.Id });
            cmd.Parameters.Add(new NpgsqlParameter { Value = d.EntryId });
            cmd.Parameters.Add(new NpgsqlParameter { Value = d.Language });
            cmd.Parameters.Add(new NpgsqlParameter { Value = d.Category });
            cmd.Parameters.Add(new NpgsqlParameter { Value = d.Question });
            cmd.Parameters.Add(new NpgsqlParameter { Value = d.Answer });
            cmd.Parameters.Add(new NpgsqlParameter { Value = d.Content });
            cmd.Parameters.Add(new NpgsqlParameter { Value = new Vector(vectors[i]) });
            await cmd.ExecuteNonQueryAsync(ct);
        }
        await tx.CommitAsync(ct);
    }

    public async Task<int> CountAsync(CancellationToken ct)
    {
        await using var cmd = db.CreateCommand("SELECT count(*) FROM faq_document");
        return Convert.ToInt32(await cmd.ExecuteScalarAsync(ct));
    }

    /// <summary>
    /// Language exists for one reason: on the full corpus, same-language matches score high
    /// enough that every Chinese passage outranks every English one, so cross-lingual
    /// retrieval is invisible. Filtering to the other language is how you find out whether
    /// it works at all -- which is what matters for an entry nobody has translated yet.
    /// </summary>
    public async Task<IReadOnlyList<Passage>> SearchAsync(float[] query, SearchOptions opts, CancellationToken ct)
    {
        const string sql = """
            SELECT id, entry_id, language, category, question, answer, content,
                   1 - (embedding <=> $1) AS score
            FROM faq_document
            WHERE ($2 = '' OR language = $2)
              AND 1 - (embedding <=> $1) >= $3
            ORDER BY embedding <=> $1
            LIMIT $4
            """;
        await using var cmd = db.CreateCommand(sql);
        cmd.Parameters.Add(new NpgsqlParameter { Value = new Vector(query) });
        cmd.Parameters.Add(new NpgsqlParameter { Value = opts.Language });
        cmd.Parameters.Add(new NpgsqlParameter { Value = opts.Threshold });
        cmd.Parameters.Add(new NpgsqlParameter { Value = opts.TopK });
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        var passages = new List<Passage>();
        while (await reader.ReadAsync(ct))
        {
            passages.Add(new Passage(new Document(
                Id: reader.GetString(0), EntryId: reader.GetString(1), Language: reader.GetString(2),
                Category: reader.GetString(3), Question: reader.GetString(4), Answer: reader.GetString(5),
                Content: reader.GetString(6), CorpusVersion: ""), reader.GetDouble(7)));
        }
        return passages;
    }
}

public static class Ingest
{
    /// <summary>
    /// Embeds the corpus and replaces what is in the store. It runs at startup: 36 documents
    /// take a few hundred milliseconds, and it keeps the deployed corpus and the file in the
    /// repository from drifting apart. A corpus large enough for that to hurt would want an
    /// offline indexing job instead.
    /// </summary>
    public static async Task<int> RunAsync(string corpusPath, IEmbedder embedder, VectorStore store,
        Microsoft.Extensions.Logging.ILogger logger, CancellationToken ct)
    {
        var corpus = Corpus.Load(corpusPath);
        var docs = corpus.Documents();
        var started = System.Diagnostics.Stopwatch.StartNew();
        var vectors = await embedder.EmbedPassagesAsync(docs.Select(d => d.Content).ToList(), ct);
        var embedded = started.Elapsed;
        await store.ReplaceAsync(docs, vectors, ct);
        Microsoft.Extensions.Logging.LoggerExtensions.LogInformation(logger,
            "ingested FAQ corpus: {Documents} documents from {Entries} entries, version {Version}, embedded in {EmbedMs} ms",
            docs.Count, corpus.Entries.Count, corpus.Version, (long)embedded.TotalMilliseconds);
        return docs.Count;
    }
}
