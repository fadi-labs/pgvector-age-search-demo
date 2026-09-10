using BookSearchDemo.Data;
using BookSearchDemo.Models;
using BookSearchDemo.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace BookSearchDemo;

class Program
{
    static async Task Main(string[] args)
    {
        var config = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: false, reloadOnChange: false)
            .AddJsonFile("appsettings.Development.json", optional: true, reloadOnChange: false)
            .AddUserSecrets<Program>(optional: true)
            .AddEnvironmentVariables()
            .Build();

        var model = config["OPENROUTER_EMBEDDING_MODEL"];
        var dimensionsStr = config["EMBEDDING_DIMENSIONS"];
        var apiKey = config["OPENROUTER_API_KEY"];

        if (string.IsNullOrEmpty(model))
        {
            Console.WriteLine("ERROR: OPENROUTER_EMBEDDING_MODEL must be set in appsettings or environment variables");
            return;
        }
        if (string.IsNullOrEmpty(dimensionsStr) || !int.TryParse(dimensionsStr, out var dimensions) || dimensions <= 0)
        {
            Console.WriteLine("ERROR: EMBEDDING_DIMENSIONS must be set in appsettings or environment variables");
            return;
        }
        if (string.IsNullOrEmpty(apiKey))
        {
            Console.WriteLine("ERROR: OPENROUTER_API_KEY must be set in appsettings or environment variables");
            return;
        }

        Console.WriteLine($"Using model: {model}, dimensions: {dimensions}");

        var connectionString = config["POSTGRES_CONNECTION_STRING"] ?? throw new InvalidOperationException("POSTGRES_CONNECTION_STRING is not set");
        var graphName = config["GRAPH_NAME"] ?? "books_graph";

        var dbContextOptions = new DbContextOptionsBuilder<BookDbContext>()
            .UseNpgsql(connectionString, o => o.UseVector())
            .Options;
        await using var dbContext = new BookDbContext(dbContextOptions);

        IBookRepository repo = new BookRepositoryEfCore(dbContext, graphName, dimensions);
        using var embeddingClient = new OpenRouterEmbeddingClient(config);

        Console.WriteLine("Database verified.");

        IReadOnlyList<Book> books;
        var doReset = AskYesNo("Generate and reset all data (books, embeddings, similarity graph)?");
        if (doReset)
        {
            var generated = GenerateBooks();
            await repo.UpsertBooksAsync(generated, CancellationToken.None);
            Console.WriteLine($"Books upserted ({generated.Count} books).");

            var texts = generated.Select(b => $"{b.Title}\n{string.Join(", ", b.Authors)}\n{string.Join(", ", b.Categories)}\n{b.Description}").ToList();
            var embeddings = await embeddingClient.GetEmbeddingsAsync(texts, CancellationToken.None);
            for (var i = 0; i < generated.Count; i++)
            {
                var bookEmbedding = new float[dimensions];
                for (var j = 0; j < dimensions; j++)
                {
                    bookEmbedding[j] = embeddings[i * dimensions + j];
                }
                await repo.UpdateEmbeddingAsync(generated[i].Id, bookEmbedding, CancellationToken.None);
            }
            Console.WriteLine("Embeddings updated.");

            await repo.BuildGraphAsync(generated, CancellationToken.None);
            Console.WriteLine("Graph built.");

            books = generated;
        }
        else
        {
            books = await repo.GetAllBooksAsync(CancellationToken.None);
            if (books.Count == 0)
            {
                Console.WriteLine("ERROR: No existing books found in the database. Run again and answer 'y' to generate data first.");
                return;
            }
            Console.WriteLine($"Loaded {books.Count} existing books from the database.");
        }

        var searchExamples = new[]
        {
            "a lighthearted story about friendship and adventure",
            "a gripping science fiction tale about survival",
            "a classic romance set in a grand estate",
            "a dystopian novel about government control",
            "a fantasy epic about a young wizard",
            "neuromancr"
        };

        Console.WriteLine("\nSearch examples:");
        for (var i = 0; i < searchExamples.Length; i++)
        {
            Console.WriteLine($"  {i + 1}. {searchExamples[i]}");
        }

        while (true)
        {
            Console.Write("\nEnter your search text ('exit' to quit, or press Enter to use example 1): ");
            var line = Console.ReadLine();
            if (line == null) break;

            var searchText = line.Trim();
            if (string.Equals(searchText, "exit", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(searchText, "quit", StringComparison.OrdinalIgnoreCase))
            {
                break;
            }
            if (string.IsNullOrEmpty(searchText))
            {
                searchText = searchExamples[0];
                Console.WriteLine($"Using default: \"{searchText}\"");
            }

            Console.WriteLine($"\n--- Keyword Search for \"{searchText}\" ---");
            var keywordResults = await repo.KeywordSearchAsync(searchText, 5, CancellationToken.None);
            PrintResults(keywordResults, "Keyword");

            var queryEmbedding = await embeddingClient.GetEmbeddingsAsync(new[] { searchText }, CancellationToken.None);
            var sampleEmbedding = new float[dimensions];
            for (var j = 0; j < dimensions; j++) sampleEmbedding[j] = queryEmbedding[j];

            Console.WriteLine($"\n--- Vector Search for \"{searchText}\" ---");
            var vectorResults = await repo.VectorSearchAsync(sampleEmbedding, 5, CancellationToken.None);
            PrintResults(vectorResults, "Vector");

            Console.WriteLine($"\n--- Hybrid Search for \"{searchText}\" ---");
            var hybridResults = await repo.HybridSearchAsync(searchText, sampleEmbedding, 5, CancellationToken.None);
            PrintResults(hybridResults, "Hybrid");

            Console.WriteLine($"\n--- Fuzzy Search for \"{searchText}\" ---");
            var fuzzyResults = await repo.FuzzySearchAsync(searchText, 5, CancellationToken.None);
            PrintResults(fuzzyResults, "Fuzzy");

            Console.WriteLine("\n--- Graph Search ---");
            var graphResults = await repo.GraphSearchAsync(books[0].Id, 5, CancellationToken.None);
            if (graphResults.Count == 0)
            {
                Console.WriteLine("  No results (graph may not be built yet, or no similar books found).");
            }
            foreach (var r in graphResults)
            {
                Console.WriteLine($"  [{r.Label}] {r.Title}");
            }
        }

        Console.WriteLine("\nDone.");
    }

    static bool AskYesNo(string prompt)
    {
        while (true)
        {
            Console.Write($"{prompt} (y/n): ");
            var answer = Console.ReadLine()?.Trim().ToLowerInvariant();
            if (answer == "y" || answer == "yes") return true;
            if (answer == "n" || answer == "no") return false;
            Console.WriteLine("  Please enter y or n.");
        }
    }

    static Guid DeterministicId(string title)
    {
        var hash = System.Security.Cryptography.MD5.HashData(System.Text.Encoding.UTF8.GetBytes(title));
        return new Guid(hash);
    }

    static List<Book> GenerateBooks()
    {
        var books = new List<Book>
        {
            new() { Id = DeterministicId("The Midnight Library"), Title = "The Midnight Library", Authors = new[] { "Matt Haig" }, Categories = new[] { "Fiction", "Philosophy" }, PublishedYear = 2020, Description = "Between life and death there is a library, and within that library, the shelves go on forever. Every book provides a chance to try another life you could have lived." },
            new() { Id = DeterministicId("Project Hail Mary"), Title = "Project Hail Mary", Authors = new[] { "Andy Weir" }, Categories = new[] { "Science Fiction", "Adventure" }, PublishedYear = 2021, Description = "Ryland Grace is the sole survivor on a desperate, last-chance mission—and if he fails, humanity and the earth itself will perish." },
            new() { Id = DeterministicId("The Silent Patient"), Title = "The Silent Patient", Authors = new[] { "Alex Michaelides" }, Categories = new[] { "Thriller", "Mystery" }, PublishedYear = 2019, Description = "Alicia Berenson's life is tragically cut short when she shoots her husband dead and never speaks another word." },
            new() { Id = DeterministicId("Dune"), Title = "Dune", Authors = new[] { "Frank Herbert" }, Categories = new[] { "Science Fiction", "Epic" }, PublishedYear = 1965, Description = "Set in the distant future amidst a feudal interstellar society, Dune tells the story of young Paul Atreides whose family accepts the stewardship of the desert planet Arrakis." },
            new() { Id = DeterministicId("Pride and Prejudice"), Title = "Pride and Prejudice", Authors = new[] { "Jane Austen" }, Categories = new[] { "Classic", "Romance" }, PublishedYear = 1813, Description = "It is a truth universally acknowledged, that a single man in possession of a good fortune, must be in want of a wife." },
            new() { Id = DeterministicId("Neuromancer"), Title = "Neuromancer", Authors = new[] { "William Gibson" }, Categories = new[] { "Science Fiction", "Cyberpunk" }, PublishedYear = 1984, Description = "The Matrix has you. Follow the white rabbit. Case is the hottest job in the business." },
            new() { Id = DeterministicId("The Name of the Wind"), Title = "The Name of the Wind", Authors = new[] { "Patrick Rothfuss" }, Categories = new[] { "Fantasy", "Adventure" }, PublishedYear = 2007, Description = "My name is Kvothe. You may have heard of me. Some say I am an infamous wizard. Other say I am a legendary hero." },
            new() { Id = DeterministicId("1984"), Title = "1984", Authors = new[] { "George Orwell" }, Categories = new[] { "Dystopian", "Political" }, PublishedYear = 1949, Description = "War is peace. Freedom is slavery. Ignorance is strength. Big Brother is watching you." },
            new() { Id = DeterministicId("The Great Gatsby"), Title = "The Great Gatsby", Authors = new[] { "F. Scott Fitzgerald" }, Categories = new[] { "Classic", "Tragedy" }, PublishedYear = 1925, Description = "So we beat on, boats against the current, borne back ceaselessly into the past." },
            new() { Id = DeterministicId("Brave New World"), Title = "Brave New World", Authors = new[] { "Aldous Huxley" }, Categories = new[] { "Dystopian", "Science Fiction" }, PublishedYear = 1932, Description = "Community, Identity, Stability. That is the motto of this brave new world." }
        };

        return books;
    }

    static void PrintResults(IReadOnlyList<BookSearchResult> results, string method)
    {
        if (results.Count == 0)
        {
            Console.WriteLine($"  No results from {method} search.");
            return;
        }
        foreach (var r in results)
        {
            Console.WriteLine($"  [{method}] {r.Title} (score: {r.Score:F4})");
        }
    }

}
