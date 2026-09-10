# AGENTS.md

## Repository Overview

This is a single-demo repository combining PostgreSQL (with pgvector, Apache AGE, pg_trgm) and a .NET 10 console app that generates embeddings via OpenRouter.

## Key Directories

| Path | Purpose |
|---|---|
| `db/` | PostgreSQL init scripts (mounted into container) |
| `app/` | .NET application |
| `Dockerfile.postgres` | Custom PG16 image with pgvector + AGE built from source |

## Startup Commands

```powershell
# Start PostgreSQL (from the repo root)
docker compose up -d --build

# Check container health
docker compose ps

# Stop and clean everything (including data volume)
docker compose down -v
```

- **Always use `docker compose down -v`** (not just `down`) to reset the database volume and re-run init scripts. Without `-v`, init scripts only run on a fresh volume.
- `docker compose up -d --build` is needed when the Dockerfile or build context changes.

## Configuration

Configuration is split across two files with two different purposes:

1. **`.env`** (repo root) — consumed only by `docker-compose.yml` to configure the
   PostgreSQL container itself: `POSTGRES_USER`, `POSTGRES_PASSWORD`, `POSTGRES_DB`,
   `POSTGRES_PORT`. Copy `.env.example` to `.env` and fill in real values before
   running `docker compose up`.
2. **`app/appsettings.json`** (or `app/appsettings.Development.json` /
   `dotnet user-secrets`) — consumed by the .NET app via
   `Microsoft.Extensions.Configuration` (JSON file → env vars → user secrets, in
   that override order): `POSTGRES_CONNECTION_STRING`, `OPENROUTER_BASE_URL`,
   `OPENROUTER_API_KEY`, `OPENROUTER_EMBEDDING_MODEL`, `OPENROUTER_HTTP_REFERER`,
   `OPENROUTER_X_TITLE`, `EMBEDDING_DIMENSIONS`, `GRAPH_NAME`. `POSTGRES_CONNECTION_STRING`
   must point at the same user/password/db/port set in `.env`.

**`.env` is in `.gitignore` — never commit it.** `app/appsettings.json` currently
ships without `OPENROUTER_API_KEY` filled in — it must be set locally before running
the app, and a real key must never be committed into it.

`OPENROUTER_EMBEDDING_MODEL` must be a currently available free model from the
[OpenRouter models catalog](https://openrouter.ai/models). Model identifiers and
dimensions change — always verify before finalizing schema. `EMBEDDING_DIMENSIONS`
must match the selected model's actual output dimension; it's interpolated into the
`vector(n)` column type in SQL and must never be guessed or left as a placeholder.

## PostgreSQL Container Details

- **PostgreSQL 16** with `shared_preload_libraries=age` (required by AGE).
- Extensions `vector`, `age`, and `pg_trgm` are built from source in `Dockerfile.postgres`.
- Init scripts in `db/` mount to `/docker-entrypoint-initdb.d/` and run automatically on new volumes only.
- The `001_extensions.sql` script loads AGE, creates extensions, and verifies all three are available — raises an exception if any are missing.
- AGE requires `SET search_path = ag_catalog, "$user", public;` before calling `cypher()`.

## .NET Application

- .NET 10 console app (`app/BookSearchDemo.csproj`).
- Two implementations of `IBookRepository` behind the same interface:
  `BookRepositoryEfCore` (EF Core over `BookDbContext`/`Npgsql.EntityFrameworkCore.PostgreSQL`,
  the one `Program.cs` wires up by default) and `BookRepository` (hand-written SQL via
  raw Npgsql). Both implement keyword, vector, hybrid, and graph search plus
  upsert/embedding-update/graph-build.
- Configuration via `Microsoft.Extensions.Configuration`, layered as JSON
  (`appsettings.json` → `appsettings.Development.json`) → environment variables →
  user secrets — see Configuration above.
- All SQL and Cypher must use **parameterized queries** — no string interpolation.
- The `Program.cs` flow is: validate config → connect → ask whether to reset data →
  seed books → batch embeddings via OpenRouter → update embeddings → build/refresh
  graph (or, if skipping reset, load existing books) → loop running keyword, vector,
  hybrid, and graph queries against user-entered search text.
- OpenRouter client (`OpenRouterEmbeddingClient`) must handle 429s with exponential
  backoff, never log API keys, and validate embedding dimensions against the DB column.

## Important Constraints

- **No Azure dependencies** — this is a local demo. No `azure_ai`, `pg_diskann`, or managed identity.
- **No secrets in source, logs, SQL, or committed config.**
- The demo must be idempotent — re-running must not duplicate books, graph nodes, or edges.
- Graph rebuild must explicitly clear/replace the demo graph before projection.
