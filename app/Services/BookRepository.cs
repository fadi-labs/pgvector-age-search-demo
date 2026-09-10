using System.Data;
using BookSearchDemo.Models;
using Microsoft.Extensions.Configuration;
using Npgsql;
using Pgvector;

namespace BookSearchDemo.Services;

public class BookRepository : IBookRepository
{
    private readonly NpgsqlDataSource _dataSource;
    private readonly string _graphName;
    private readonly int _embeddingDimensions;

    public BookRepository(IConfiguration config, int embeddingDimensions)
    {
        var connectionString = config["POSTGRES_CONNECTION_STRING"] ?? throw new InvalidOperationException("POSTGRES_CONNECTION_STRING is not set");
        _graphName = config["GRAPH_NAME"] ?? "books_graph";
        _embeddingDimensions = embeddingDimensions;

        var dataSourceBuilder = new NpgsqlDataSourceBuilder(connectionString);
        dataSourceBuilder.UseVector();
        _dataSource = dataSourceBuilder.Build();
    }

    public async Task UpsertBooksAsync(IReadOnlyCollection<Book> books, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);

        await using var tx = conn.BeginTransaction();
        try
        {
            foreach (var book in books)
            {
                await using var cmd = new NpgsqlCommand(@"
                    INSERT INTO books (id, title, authors, categories, published_year, description)
                    VALUES (@id, @title, @authors, @categories, @publishedYear, @description)
                    ON CONFLICT (id) DO UPDATE SET
                        title = EXCLUDED.title,
                        authors = EXCLUDED.authors,
                        categories = EXCLUDED.categories,
                        published_year = EXCLUDED.published_year,
                        description = EXCLUDED.description", conn, tx);

                cmd.Parameters.AddWithValue("id", book.Id);
                cmd.Parameters.AddWithValue("title", book.Title);
                cmd.Parameters.AddWithValue("authors", book.Authors);
                cmd.Parameters.AddWithValue("categories", book.Categories);
                cmd.Parameters.AddWithValue("publishedYear", book.PublishedYear);
                cmd.Parameters.AddWithValue("description", book.Description);
                await cmd.ExecuteNonQueryAsync(ct);
            }
            await tx.CommitAsync(ct);
        }
        catch
        {
            await tx.RollbackAsync(ct);
            throw;
        }
    }

    public async Task<IReadOnlyList<Book>> GetAllBooksAsync(CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);

        await using var cmd = new NpgsqlCommand(
            "SELECT id, title, authors, categories, published_year, description FROM books ORDER BY title", conn);

        var books = new List<Book>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            books.Add(new Book
            {
                Id = reader.GetGuid(0),
                Title = reader.GetString(1),
                Authors = reader.GetFieldValue<string[]>(2),
                Categories = reader.GetFieldValue<string[]>(3),
                PublishedYear = reader.IsDBNull(4) ? 0 : reader.GetInt32(4),
                Description = reader.GetString(5)
            });
        }
        return books;
    }

    public async Task UpdateEmbeddingAsync(Guid bookId, IReadOnlyList<float> embedding, CancellationToken ct)
    {
        if (embedding == null || embedding.Count != _embeddingDimensions)
            throw new ArgumentException($"Embedding must have {_embeddingDimensions} dimensions", nameof(embedding));

        await using var conn = await _dataSource.OpenConnectionAsync(ct);

        var paramName = "embedding";
        await using var cmd = new NpgsqlCommand(
            "UPDATE books SET embedding = @embedding WHERE id = @id", conn);
        cmd.Parameters.AddWithValue(paramName, new Vector(embedding.ToArray()));
        cmd.Parameters.AddWithValue("id", bookId);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<IReadOnlyList<BookSearchResult>> KeywordSearchAsync(string query, int limit, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);

        await using var cmd = new NpgsqlCommand(@"
            SELECT id, title, ts_rank_cd(search_document, websearch_to_tsquery('english', @query)) AS score
            FROM books
            WHERE search_document @@ websearch_to_tsquery('english', @query)
            ORDER BY search_document @@ websearch_to_tsquery('english', @query) DESC, score DESC
            LIMIT @limit", conn);
        cmd.Parameters.AddWithValue("query", query);
        cmd.Parameters.AddWithValue("limit", limit);

        var results = new List<BookSearchResult>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            results.Add(new BookSearchResult
            {
                Id = reader.GetGuid(0),
                Title = reader.GetString(1),
                Score = reader.GetDouble(2)
            });
        }
        return results;
    }

    public async Task<IReadOnlyList<BookSearchResult>> VectorSearchAsync(IReadOnlyList<float> embedding, int limit, CancellationToken ct)
    {
        if (embedding == null || embedding.Count != _embeddingDimensions)
            throw new ArgumentException($"Embedding must have {_embeddingDimensions} dimensions", nameof(embedding));

        await using var conn = await _dataSource.OpenConnectionAsync(ct);

        await using var cmd = new NpgsqlCommand(@"
            SELECT id, title, 1 - (embedding <=> @query_embedding) AS score
            FROM books
            WHERE embedding IS NOT NULL
            ORDER BY embedding <=> @query_embedding
            LIMIT @limit", conn);
        cmd.Parameters.AddWithValue("query_embedding", new Vector(embedding.ToArray()));
        cmd.Parameters.AddWithValue("limit", limit);

        var results = new List<BookSearchResult>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            results.Add(new BookSearchResult
            {
                Id = reader.GetGuid(0),
                Title = reader.GetString(1),
                Score = reader.GetDouble(2)
            });
        }
        return results;
    }

    public async Task<IReadOnlyList<BookSearchResult>> HybridSearchAsync(string query, IReadOnlyList<float> embedding, int limit, CancellationToken ct)
    {
        if (embedding == null || embedding.Count != _embeddingDimensions)
            throw new ArgumentException($"Embedding must have {_embeddingDimensions} dimensions", nameof(embedding));

        await using var conn = await _dataSource.OpenConnectionAsync(ct);

        await using var cmd = new NpgsqlCommand(@"
            WITH keyword_results AS (
                SELECT id, title, ts_rank_cd(search_document, websearch_to_tsquery('english', @query)) AS rank
                FROM books
                WHERE search_document @@ websearch_to_tsquery('english', @query)
                ORDER BY rank DESC
                LIMIT @limit
            ),
            vector_results AS (
                SELECT id, title, 1 - (embedding <=> @query_embedding) AS rank
                FROM books
                WHERE embedding IS NOT NULL
                ORDER BY embedding <=> @query_embedding
                LIMIT @limit
            )
            SELECT COALESCE(k.id, v.id), COALESCE(k.title, v.title),
                   1.0 / (60 + COALESCE(k.rank, 99999)) + 1.0 / (60 + COALESCE(v.rank, 99999)) AS score
            FROM keyword_results k
            FULL OUTER JOIN vector_results v ON k.id = v.id
            ORDER BY score DESC
            LIMIT @limit", conn);
        cmd.Parameters.AddWithValue("query", query);
        cmd.Parameters.AddWithValue("query_embedding", new Vector(embedding.ToArray()));
        cmd.Parameters.AddWithValue("limit", limit);

        var results = new List<BookSearchResult>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            results.Add(new BookSearchResult
            {
                Id = reader.GetGuid(0),
                Title = reader.GetString(1),
                Score = reader.GetDouble(2)
            });
        }
        return results;
    }

    public async Task<IReadOnlyList<GraphSearchResult>> GraphSearchAsync(Guid seedBookId, int limit, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);

        var escapedSeed = seedBookId.ToString();
        await using var cmd = new NpgsqlCommand($@"
            SET search_path = ag_catalog, ""$user"", public;
            SELECT id::text, title::text, disp_label::text FROM cypher('{_graphName}', $$
                MATCH (b:Book {{id: '{escapedSeed}'}})-[:SIMILAR_TO*1..3]->(related:Book)
                WHERE related.id <> b.id
                RETURN DISTINCT related.id AS id, related.title AS title, related.disp_label AS disp_label
                LIMIT {limit}
            $$) AS (id agtype, title agtype, disp_label agtype);", conn);

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
        await using var conn = await _dataSource.OpenConnectionAsync(ct);

        await using var tx = conn.BeginTransaction();
        try
        {
            await tx.SaveAsync("before_create", ct);
            await using var cmd = new NpgsqlCommand($@"
                SET search_path = ag_catalog, ""$user"", public;
                SELECT ag_catalog.create_graph('{_graphName}');", conn, tx);
            try { await cmd.ExecuteNonQueryAsync(ct); }
            catch { await tx.RollbackAsync("before_create", ct); /* Graph may already exist */ }

            await tx.SaveAsync("before_drop", ct);
            await using var clearCmd = new NpgsqlCommand($@"
                SET search_path = ag_catalog, ""$user"", public;
                SELECT ag_catalog.drop_graph('{_graphName}', true);", conn, tx);
            try { await clearCmd.ExecuteNonQueryAsync(ct); }
            catch { await tx.RollbackAsync("before_drop", ct); }

            await using var createCmd = new NpgsqlCommand($@"
                SET search_path = ag_catalog, ""$user"", public;
                SELECT ag_catalog.create_graph('{_graphName}');", conn, tx);
            await createCmd.ExecuteNonQueryAsync(ct);

            foreach (var book in books)
            {
                var authorsCypher = book.Authors.Select(a => $"('{a.Replace("'", "''")}', 'Author', '{{disp_label: '{a.Replace("'", "''")}'}}')").ToList();
                var categoriesCypher = book.Categories.Select(c => $"('{c.Replace("'", "''")}', 'Category', '{{disp_label: '{c.Replace("'", "''")}'}}')").ToList();
                var decade = book.PublishedYear / 10 * 10;

                var authorsValues = string.Join(", ", authorsCypher);
                var categoriesValues = string.Join(", ", categoriesCypher);

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
                    $$) AS (v agtype);", conn, tx);
                await insertNode.ExecuteNonQueryAsync(ct);
            }

            await using var clearEdgesCmd = new NpgsqlCommand($@"
                SET search_path = ag_catalog, ""$user"", public;
                SELECT * FROM cypher('{_graphName}', $$
                    MATCH ()-[r:SIMILAR_TO]->()
                    DELETE r
                $$) AS (v agtype);", conn, tx);
            await clearEdgesCmd.ExecuteNonQueryAsync(ct);

            foreach (var book in books)
            {
                await using var similarBooksCmd = new NpgsqlCommand(@"
                    SELECT other.id
                    FROM books b
                    JOIN books other ON other.id != b.id
                    WHERE b.id = @bookId AND b.embedding IS NOT NULL AND other.embedding IS NOT NULL
                    ORDER BY b.embedding <=> other.embedding
                    LIMIT 5", conn, tx);
                similarBooksCmd.Parameters.AddWithValue("bookId", book.Id);

                var similarIds = new List<Guid>();
                await using (var reader = await similarBooksCmd.ExecuteReaderAsync(ct))
                {
                    while (await reader.ReadAsync(ct))
                    {
                        similarIds.Add(reader.GetGuid(0));
                    }
                }

                foreach (var otherId in similarIds)
                {
                    await using var edgeCmd = new NpgsqlCommand($@"
                        SET search_path = ag_catalog, ""$user"", public;
                        SELECT * FROM cypher('{_graphName}', $$
                            MATCH (b:Book {{id: '{book.Id}'}}), (other:Book {{id: '{otherId}'}})
                            CREATE (b)-[:SIMILAR_TO]->(other)
                        $$) AS (v agtype);", conn, tx);
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

    private static string EscapeCypherString(string value) =>
        value.Replace("\\", "\\\\").Replace("'", "\\'");
}
