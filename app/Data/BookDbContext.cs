using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

namespace BookSearchDemo.Data;

public class BookDbContext : DbContext
{
    public DbSet<BookEntity> Books => Set<BookEntity>();

    public BookDbContext(DbContextOptions<BookDbContext> options) : base(options)
    {
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasPostgresExtension("vector");

        modelBuilder.Entity<BookEntity>(book =>
        {
            book.ToTable("books");
            book.HasKey(b => b.Id);

            book.Property(b => b.Id).HasColumnName("id");
            book.Property(b => b.Title).HasColumnName("title");
            book.Property(b => b.Authors).HasColumnName("authors");
            book.Property(b => b.Categories).HasColumnName("categories");
            book.Property(b => b.PublishedYear).HasColumnName("published_year");
            book.Property(b => b.Description).HasColumnName("description");
            book.Property(b => b.Embedding).HasColumnName("embedding").HasColumnType("vector(1024)");
            book.Property(b => b.CreatedAt).HasColumnName("created_at").ValueGeneratedOnAdd();

            // search_document is maintained by the books_search_document_trigger; EF never writes it.
            book.Property(b => b.SearchDocument)
                .HasColumnName("search_document")
                .ValueGeneratedOnAddOrUpdate()
                .Metadata.SetBeforeSaveBehavior(PropertySaveBehavior.Ignore);
        });
    }
}
