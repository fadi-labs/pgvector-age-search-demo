using NpgsqlTypes;
using Pgvector;

namespace BookSearchDemo.Data;

public class BookEntity
{
    public Guid Id { get; set; }
    public string Title { get; set; } = string.Empty;
    public string[] Authors { get; set; } = Array.Empty<string>();
    public string[] Categories { get; set; } = Array.Empty<string>();
    public int? PublishedYear { get; set; }
    public string Description { get; set; } = string.Empty;
    public NpgsqlTsVector? SearchDocument { get; set; }
    public Vector? Embedding { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}
