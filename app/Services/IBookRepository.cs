using BookSearchDemo.Models;

namespace BookSearchDemo.Services;

public interface IBookRepository
{
    Task UpsertBooksAsync(IReadOnlyCollection<Book> books, CancellationToken ct);
    Task<IReadOnlyList<Book>> GetAllBooksAsync(CancellationToken ct);
    Task UpdateEmbeddingAsync(Guid bookId, IReadOnlyList<float> embedding, CancellationToken ct);
    Task<IReadOnlyList<BookSearchResult>> KeywordSearchAsync(string query, int limit, CancellationToken ct);
    Task<IReadOnlyList<BookSearchResult>> VectorSearchAsync(IReadOnlyList<float> embedding, int limit, CancellationToken ct);
    Task<IReadOnlyList<BookSearchResult>> HybridSearchAsync(string query, IReadOnlyList<float> embedding, int limit, CancellationToken ct);
    Task<IReadOnlyList<GraphSearchResult>> GraphSearchAsync(Guid seedBookId, int limit, CancellationToken ct);
    Task BuildGraphAsync(IReadOnlyCollection<Book> books, CancellationToken ct);
}
