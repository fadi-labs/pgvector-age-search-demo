SET search_path = ag_catalog, "$user", public;

DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM ag_catalog.ag_graph WHERE name = 'books_graph') THEN
        PERFORM ag_catalog.create_graph('books_graph');
    END IF;
END
$$;

SELECT name FROM ag_catalog.ag_graph ORDER BY name;
