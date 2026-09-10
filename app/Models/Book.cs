namespace BookSearchDemo.Models;

public class Book
{
    public Guid Id { get; set; }
    public string Title { get; set; } = string.Empty;
    public string[] Authors { get; set; } = Array.Empty<string>();
    public string[] Categories { get; set; } = Array.Empty<string>();
    public int PublishedYear { get; set; }
    public string Description { get; set; } = string.Empty;
    public float[]? Embedding { get; set; }
}

public class BookSearchResult
{
    public Guid Id { get; set; }
    public string Title { get; set; } = string.Empty;
    public double Score { get; set; }
}

public class GraphSearchResult
{
    public Guid Id { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
}
