LOAD 'age';

CREATE EXTENSION IF NOT EXISTS vector;
CREATE EXTENSION IF NOT EXISTS age;
CREATE EXTENSION IF NOT EXISTS pg_trgm;

SET search_path = ag_catalog, "$user", public;

DO $$
DECLARE
    missing_extensions text;
BEGIN
    SELECT string_agg(required.name, ', ' ORDER BY required.name)
    INTO missing_extensions
    FROM (VALUES ('vector'), ('age'), ('pg_trgm')) AS required(name)
    WHERE NOT EXISTS (
        SELECT 1
        FROM pg_extension installed
        WHERE installed.extname = required.name
    );

    IF missing_extensions IS NOT NULL THEN
        RAISE EXCEPTION 'Required PostgreSQL extensions are unavailable: %', missing_extensions;
    END IF;
END
$$;

SELECT name, default_version
FROM pg_available_extensions
WHERE name IN ('vector', 'age', 'pg_trgm')
ORDER BY name;

