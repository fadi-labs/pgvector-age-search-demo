using BookSearchDemo.Data;
using BookSearchDemo.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;
using Pgvector;
using Pgvector.EntityFrameworkCore;

namespace BookSearchDemo.Services;

public class BookRepositoryEfCore : IBookRepository
{
    private const double RrfK = 60;

    private readonly BookDbContext _db;
    private readonly string _graphName;
    private readonly int _embeddingDimensions;

    public BookRepositoryEfCore(BookDbContext db, string graphName, int embeddingDimensions)
    {
        _db = db;
        _graphName = graphName;
        _embeddingDimensions = embeddingDimensions;
    }

    public async Task UpsertBooksAsync(IReadOnlyCollection<Book> books, CancellationToken ct)
    {
        var ids = books.Select(b => b.Id).ToList();
        var existing = await _db.Books.Where(b => ids.Contains(b.Id)).ToDictionaryAsync(b => b.Id, ct);

        foreach (var book in books)
        {
            if (existing.TryGetValue(book.Id, out var entity))
            {
                entity.Title = book.Title;
                entity.Authors = book.Authors;
                entity.Categories = book.Categories;
                entity.PublishedYear = book.PublishedYear;
                entity.Description = book.Description;
            }
            else
            {
                _db.Books.Add(new BookEntity
                {
                    Id = book.Id,
                    Title = book.Title,
                    Authors = book.Authors,
                    Categories = book.Categories,
                    PublishedYear = book.PublishedYear,
                    Description = book.Description
                });
            }
        }

        await _db.SaveChangesAsync(ct);
    }

    public async Task<IReadOnlyList<Book>> GetAllBooksAsync(CancellationToken ct) =>
        await _db.Books.AsNoTracking()
            .OrderBy(b => b.Title)
            .Select(b => new Book
            {
                Id = b.Id,
                Title = b.Title,
                Authors = b.Authors,
                Categories = b.Categories,
                PublishedYear = b.PublishedYear ?? 0,
                Description = b.Description
            })
            .ToListAsync(ct);

    public async Task UpdateEmbeddingAsync(Guid bookId, IReadOnlyList<float> embedding, CancellationToken ct)
    {
        if (embedding == null || embedding.Count != _embeddingDimensions)
            throw new ArgumentException($"Embedding must have {_embeddingDimensions} dimensions", nameof(embedding));

        var vector = new Vector(embedding.ToArray());
        await _db.Books
            .Where(b => b.Id == bookId)
            .ExecuteUpdateAsync(setters => setters.SetProperty(b => b.Embedding, vector), ct);
    }

    public async Task<IReadOnlyList<BookSearchResult>> KeywordSearchAsync(string query, int limit, CancellationToken ct)
    {
        return await _db.Books.AsNoTracking()
            .Where(b => b.SearchDocument!.Matches(EF.Functions.WebSearchToTsQuery("english", query)))
            .Select(b => new BookSearchResult
            {
                Id = b.Id,
                Title = b.Title,
                Score = b.SearchDocument!.RankCoverDensity(EF.Functions.WebSearchToTsQuery("english", query))
            })
            .OrderByDescending(r => r.Score)
            .Take(limit)
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<BookSearchResult>> VectorSearchAsync(IReadOnlyList<float> embedding, int limit, CancellationToken ct)
    {
        if (embedding == null || embedding.Count != _embeddingDimensions)
            throw new ArgumentException($"Embedding must have {_embeddingDimensions} dimensions", nameof(embedding));

        var queryVector = new Vector(embedding.ToArray());

        return await _db.Books.AsNoTracking()
            .Where(b => b.Embedding != null)
            .Select(b => new BookSearchResult
            {
                Id = b.Id,
                Title = b.Title,
                Score = 1 - b.Embedding!.CosineDistance(queryVector)
            })
            .OrderByDescending(r => r.Score)
            .Take(limit)
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<BookSearchResult>> HybridSearchAsync(string query, IReadOnlyList<float> embedding, int limit, CancellationToken ct)
    {
        if (embedding == null || embedding.Count != _embeddingDimensions)
            throw new ArgumentException($"Embedding must have {_embeddingDimensions} dimensions", nameof(embedding));

        var queryVector = new Vector(embedding.ToArray());

        var keywordResults = await _db.Books.AsNoTracking()
            .Where(b => b.SearchDocument!.Matches(EF.Functions.WebSearchToTsQuery("english", query)))
            .Select(b => new { b.Id, b.Title, Rank = (double)b.SearchDocument!.RankCoverDensity(EF.Functions.WebSearchToTsQuery("english", query)) })
            .OrderByDescending(r => r.Rank)
            .Take(limit)
            .ToListAsync(ct);

        var vectorResults = await _db.Books.AsNoTracking()
            .Where(b => b.Embedding != null)
            .Select(b => new { b.Id, b.Title, Rank = 1.0 - b.Embedding!.CosineDistance(queryVector) })
            .OrderByDescending(r => r.Rank)
            .Take(limit)
            .ToListAsync(ct);

        var keywordById = keywordResults.ToDictionary(r => r.Id);
        var vectorById = vectorResults.ToDictionary(r => r.Id);

        var results = keywordById.Keys.Union(vectorById.Keys)
            .Select(id =>
            {
                var title = keywordById.TryGetValue(id, out var kw) ? kw.Title : vectorById[id].Title;
                var keywordRank = keywordById.TryGetValue(id, out kw) ? kw.Rank : 99999;
                var vectorRank = vectorById.TryGetValue(id, out var ve) ? ve.Rank : 99999;
                var score = 1.0 / (RrfK + keywordRank) + 1.0 / (RrfK + vectorRank);
                return new BookSearchResult { Id = id, Title = title, Score = score };
            })
            .OrderByDescending(r => r.Score)
            .Take(limit)
            .ToList();

        return results;
    }

    public async Task<IReadOnlyList<BookSearchResult>> FuzzySearchAsync(string query, int limit, CancellationToken ct)
    {
        return await _db.Books.AsNoTracking()
            .Where(b => EF.Functions.TrigramsAreSimilar(b.Title, query))
            .Select(b => new BookSearchResult
            {
                Id = b.Id,
                Title = b.Title,
                Score = EF.Functions.TrigramsSimilarity(b.Title, query)
            })
            .OrderByDescending(r => r.Score)
            .Take(limit)
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<GraphSearchResult>> GraphSearchAsync(Guid seedBookId, int limit, CancellationToken ct)
    {
        var connection = await OpenRawConnectionAsync(ct);

        await using var cmd = new NpgsqlCommand($@"
            SET search_path = ag_catalog, ""$user"", public;
            SELECT id::text, title::text, disp_label::text FROM cypher('{_graphName}', $$
                MATCH (b:Book {{id: '{seedBookId}'}})-[:SIMILAR_TO*1..3]->(related:Book)
                WHERE related.id <> b.id
                RETURN DISTINCT related.id AS id, related.title AS title, related.disp_label AS disp_label
                LIMIT {limit}
            $$) AS (id agtype, title agtype, disp_label agtype);", connection);

        var results = new List<GraphSearchResult>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var idStr = reader.IsDBNull(0) ? "" : reader.GetString(0).Trim('"');
            var title = reader.IsDBNull(1) ? "" : reader.GetString(1).Trim('"');
            var label = reader.IsDBNull(2) ? "" : reader.GetString(2).Trim('"');
            if (Guid.TryParse(idStr, out var id))
            {
                results.Add(new GraphSearchResult { Id = id, Title = title, Label = label });
            }
        }
        return results;
    }

    public async Task BuildGraphAsync(IReadOnlyCollection<Book> books, CancellationToken ct)
    {
        var connection = await OpenRawConnectionAsync(ct);

        await using var tx = await _db.Database.BeginTransactionAsync(ct);
        var npgsqlTx = (NpgsqlTransaction)tx.GetDbTransaction();
        try
        {
            await npgsqlTx.SaveAsync("before_create", ct);
            await using var cmd = new NpgsqlCommand($@"
                SET search_path = ag_catalog, ""$user"", public;
                SELECT ag_catalog.create_graph('{_graphName}');", connection, npgsqlTx);
            try { await cmd.ExecuteNonQueryAsync(ct); }
            catch { await npgsqlTx.RollbackAsync("before_create", ct); /* Graph may already exist */ }

            await npgsqlTx.SaveAsync("before_drop", ct);
            await using var clearCmd = new NpgsqlCommand($@"
                SET search_path = ag_catalog, ""$user"", public;
                SELECT ag_catalog.drop_graph('{_graphName}', true);", connection, npgsqlTx);
            try { await clearCmd.ExecuteNonQueryAsync(ct); }
            catch { await npgsqlTx.RollbackAsync("before_drop", ct); }

            await using var createCmd = new NpgsqlCommand($@"
                SET search_path = ag_catalog, ""$user"", public;
                SELECT ag_catalog.create_graph('{_graphName}');", connection, npgsqlTx);
            await createCmd.ExecuteNonQueryAsync(ct);

            foreach (var book in books)
            {
                var decade = book.PublishedYear / 10 * 10;

                await using var insertNode = new NpgsqlCommand($@"
                    SET search_path = ag_catalog, ""$user"", public;
                    SELECT * FROM cypher('{_graphName}', $$
                        CREATE (b:Book {{id: '{book.Id}', title: '{EscapeCypherString(book.Title)}', description: '{EscapeCypherString(book.Description)}', disp_label: '{EscapeCypherString(book.Title)}'}})
                        CREATE (a:Author {{id: 'author_{book.Id}_{book.Authors[0]}', disp_label: '{EscapeCypherString(book.Authors[0])}'}})
                        CREATE (c:Category {{id: 'category_{book.Id}_{book.Categories[0]}', disp_label: '{EscapeCypherString(book.Categories[0])}'}})
                        CREATE (d:Decade {{id: 'decade_{decade}', disp_label: '{decade}s'}})
                        CREATE (b)-[:WROTE]->(a)
                        CREATE (b)-[:IN_CATEGORY]->(c)
                        CREATE (b)-[:PUBLISHED_IN]->(d)
                    $$) AS (v agtype);", connection, npgsqlTx);
                await insertNode.ExecuteNonQueryAsync(ct);
            }

            await using var clearEdgesCmd = new NpgsqlCommand($@"
                SET search_path = ag_catalog, ""$user"", public;
                SELECT * FROM cypher('{_graphName}', $$
                    MATCH ()-[r:SIMILAR_TO]->()
                    DELETE r
                $$) AS (v agtype);", connection, npgsqlTx);
            await clearEdgesCmd.ExecuteNonQueryAsync(ct);

            foreach (var book in books)
            {
                var seedEmbedding = await _db.Books.AsNoTracking()
                    .Where(b => b.Id == book.Id)
                    .Select(b => b.Embedding)
                    .FirstOrDefaultAsync(ct);
                if (seedEmbedding == null) continue;

                var similarIds = await _db.Books.AsNoTracking()
                    .Where(b => b.Id != book.Id && b.Embedding != null)
                    .OrderBy(b => b.Embedding!.CosineDistance(seedEmbedding))
                    .Select(b => b.Id)
                    .Take(5)
                    .ToListAsync(ct);

                foreach (var otherId in similarIds)
                {
                    await using var edgeCmd = new NpgsqlCommand($@"
                        SET search_path = ag_catalog, ""$user"", public;
                        SELECT * FROM cypher('{_graphName}', $$
                            MATCH (b:Book {{id: '{book.Id}'}}), (other:Book {{id: '{otherId}'}})
                            CREATE (b)-[:SIMILAR_TO]->(other)
                        $$) AS (v agtype);", connection, npgsqlTx);
                    await edgeCmd.ExecuteNonQueryAsync(ct);
                }
            }

            await tx.CommitAsync(ct);
        }
        catch
        {
            await tx.RollbackAsync(ct);
            throw;
        }
    }

    private async Task<NpgsqlConnection> OpenRawConnectionAsync(CancellationToken ct)
    {
        var connection = (NpgsqlConnection)_db.Database.GetDbConnection();
        if (connection.State != System.Data.ConnectionState.Open)
        {
            await _db.Database.OpenConnectionAsync(ct);
        }
        return connection;
    }

    private static string EscapeCypherString(string value) =>
        value.Replace("\\", "\\\\").Replace("'", "\\'");
}
