CREATE INDEX IF NOT EXISTS books_search_document_idx
    ON books USING gin (search_document);

CREATE INDEX IF NOT EXISTS books_embedding_hnsw_idx
    ON books USING hnsw (embedding vector_cosine_ops);

CREATE INDEX IF NOT EXISTS books_title_trgm_idx
    ON books USING gin (title gin_trgm_ops);
