CREATE TABLE IF NOT EXISTS books (
    id              uuid PRIMARY KEY,
    title           text NOT NULL,
    authors         text[] NOT NULL DEFAULT '{}',
    categories      text[] NOT NULL DEFAULT '{}',
    published_year  integer,
    description     text NOT NULL,
    search_document tsvector,
    embedding       vector(1024),
    created_at      timestamptz NOT NULL DEFAULT now()
);

CREATE OR REPLACE FUNCTION update_search_document()
RETURNS TRIGGER AS $$
BEGIN
    NEW.search_document :=
        setweight(to_tsvector('english', coalesce(NEW.title, '')), 'A') ||
        setweight(to_tsvector('english', coalesce(array_to_string(NEW.authors, ' '), '')), 'B') ||
        setweight(to_tsvector('english', coalesce(array_to_string(NEW.categories, ' '), '')), 'B') ||
        setweight(to_tsvector('english', coalesce(NEW.description, '')), 'C');
    RETURN NEW;
END;
$$ LANGUAGE plpgsql;

CREATE TRIGGER books_search_document_trigger
    BEFORE INSERT OR UPDATE ON books
    FOR EACH ROW
    EXECUTE FUNCTION update_search_document();
