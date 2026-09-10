# PostgreSQL Search Strategies Playground

## Why Search Matters for AI Agents

AI agents are only as effective as the information they can retrieve.

When an agent needs to search a large dataset, sending everything to an LLM is
expensive, slow, and often produces worse results. A better approach is to
retrieve the smallest, most relevant set of information first, and only then
provide that context to the model.

This repository explores different PostgreSQL search strategies that can be used
individually or combined into a hybrid search approach:

- **Full-Text Search (FTS)** — lexical search with linguistic processing such as stemming and ranking
- **Vector Search (`pgvector`)** — semantic search based on meaning rather than exact words
- **Hybrid Search** — combines FTS and vector search to benefit from both lexical precision and semantic similarity
- **Fuzzy Search (`pg_trgm`)** — typo-tolerant matching based on character trigram overlap
- **Graph Search (Apache AGE)** — discovers relationships between entities

For AI agents and RAG systems, hybrid retrieval can provide better context than
relying on embeddings alone. The goal is to retrieve better context with less
noise, reducing the amount of data sent to the LLM and potentially reducing
token usage, latency, and cost.

Search first. Reason second.

This repo is a hands-on playground for comparing five different ways to search the
same data in PostgreSQL, all running against one small seed dataset of 10 books:

| Strategy | How it matches | Powered by |
|---|---|---|
| **Keyword (full-text) search** | Literal words, normalized (stemmed, stopword-filtered) | PostgreSQL `tsvector` / `tsquery` |
| **Vector (semantic) search** | Meaning, via embedding distance | `pgvector` + an OpenRouter embedding model |
| **Hybrid search** | Both of the above, blended | `pgvector` + `tsvector`, combined client-side |
| **Fuzzy search** | Character-level similarity, tolerant of typos | PostgreSQL `pg_trgm` |
| **Graph search** | Relationships between books | Apache AGE (Cypher), fed by `pgvector` |

The runnable demo is a .NET 10 console app + a single PostgreSQL container with
`pgvector`, Apache AGE, and `pg_trgm` installed (see "Running these yourself" below
for setup and how to run it). This document is about *what the five strategies
actually do differently* and demonstrates it with real output against the seed data.

The app ships with **two implementations of the same `IBookRepository` interface**:
`BookRepositoryEfCore` (raw LINQ/EF Core over `BookDbContext`) and `BookRepository`
(hand-written SQL via Npgsql). `Program.cs` wires up `BookRepositoryEfCore` by
default — swap the type it constructs to `BookRepository` if you want to see the
same searches expressed as raw SQL instead.

## The seed data

Ten books, upserted with deterministic IDs (so re-running the app updates the same
rows rather than creating duplicates):

| Title | Categories | Description |
|---|---|---|
| The Midnight Library | Fiction, Philosophy | "...within that library, the shelves go on forever. Every book provides a chance to try another life you could have lived." |
| Project Hail Mary | Science Fiction, Adventure | "Ryland Grace is the sole survivor on a desperate, last-chance mission..." |
| The Silent Patient | Thriller, Mystery | "Alicia Berenson's life is tragically cut short when she shoots her husband dead and never speaks another word." |
| Dune | Science Fiction, Epic | "...Dune tells the story of young Paul Atreides whose family accepts the stewardship of the desert planet Arrakis." |
| Pride and Prejudice | Classic, Romance | "It is a truth universally acknowledged, that a single man in possession of a good fortune, must be in want of a wife." |
| Neuromancer | Science Fiction, Cyberpunk | "The Matrix has you. Follow the white rabbit. Case is the hottest job in the business." |
| The Name of the Wind | Fantasy, Adventure | "...Some say I am an infamous wizard. Other say I am a legendary hero." |
| 1984 | Dystopian, Political | "War is peace. Freedom is slavery. Ignorance is strength. Big Brother is watching you." |
| The Great Gatsby | Classic, Tragedy | "So we beat on, boats against the current, borne back ceaselessly into the past." |
| Brave New World | Dystopian, Science Fiction | "Community, Identity, Stability. That is the motto of this brave new world." |

Every example below is real output captured by running the app end to end (not
hand-written).

## 1. Keyword search benefits from PostgreSQL's normalizer (stemming)

`KeywordSearchAsync` runs the query through `websearch_to_tsquery('english', ...)`
against a `tsvector` built from title (weight A), authors/categories (weight B), and
description (weight C). PostgreSQL's English text search dictionary **stems** words
before comparing them — plurals, verb tenses, etc. collapse to the same root — so a
query doesn't need to use the exact inflection the source text uses.

Query: `"wizards and heroes"`

```
--- Keyword Search for "wizards and heroes" ---
  [Keyword] The Name of the Wind (score: 0.0286)
```

The book's description says "an infamous **wizard**... a legendary **hero**"
(singular) — the query used the plurals "**wizards**" and "**heroes**". Both stem to
the same root (`wizard`, `hero`) as the singular forms in the text, so they match even
though the literal strings differ. A naive `LIKE '%wizards%'` search would have missed
this entirely.

## 2. Keyword search's weakness: it's an AND of every term

`websearch_to_tsquery` treats unquoted words as an **AND** — every stemmed term must
be present somewhere in the document. This makes it brittle for natural-language,
paraphrased queries, where vector search doesn't have the same constraint.

Query: `"acknowledging painful truths"`

```
--- Keyword Search for "acknowledging painful truths" ---
  No results from Keyword search.

--- Vector Search for "acknowledging painful truths" ---
  [Vector] Pride and Prejudice (score: 0.1854)
  [Vector] 1984 (score: 0.1160)
  [Vector] The Silent Patient (score: 0.1132)
  [Vector] The Great Gatsby (score: 0.0587)
  [Vector] Brave New World (score: 0.0231)
```

*Pride and Prejudice*'s "a truth universally **acknowledged**" stems to the same root
as "**acknowledging**" — but the query also requires "**painful**", which doesn't
appear (stemmed or otherwise) anywhere in the corpus. Because it's an AND, one
missing term zeroes out the whole result set. Vector search has no such all-or-nothing
requirement — it ranks by overall semantic closeness, so *Pride and Prejudice* still
comes out clearly on top.

The same pattern shows up even more starkly here — note that "cyberpunk" is a literal,
exact match against *Neuromancer*'s category, and keyword search *still* returns nothing:

Query: `"a cyberpunk hacker navigating virtual reality"`

```
--- Keyword Search for "a cyberpunk hacker navigating virtual reality" ---
  No results from Keyword search.

--- Vector Search for "a cyberpunk hacker navigating virtual reality" ---
  [Vector] Neuromancer (score: 0.2998)
  [Vector] Project Hail Mary (score: 0.2570)
  [Vector] 1984 (score: 0.1515)
  [Vector] Brave New World (score: 0.1351)
  [Vector] Dune (score: 0.1333)
```

"hacker", "navigating", "virtual", and "reality" appear nowhere in the corpus, so the
AND fails despite the "cyberpunk" hit.

## 3. Vector search finds meaning with zero literal word overlap

This is the case embeddings are actually for: a paraphrase that shares essentially no
vocabulary with the source text.

Query: `"a warm story about second chances and alternate lives"`

```
--- Keyword Search for "a warm story about second chances and alternate lives" ---
  No results from Keyword search.

--- Vector Search for "a warm story about second chances and alternate lives" ---
  [Vector] The Midnight Library (score: 0.2449)
  [Vector] Neuromancer (score: 0.2009)
  [Vector] Brave New World (score: 0.1908)
  [Vector] Project Hail Mary (score: 0.1477)
  [Vector] Dune (score: 0.1381)
```

*The Midnight Library*'s actual description ("shelves go on forever... another life
you could have lived") shares no stems with "warm", "story", "second", "chances",
"alternate", or "lives" — yet it's the clear top match, because
`VectorSearchAsync` compares the OpenRouter embedding of the query against each book's
stored embedding via `pgvector`'s `<=>` distance operator, not against literal text.

Like keyword search, the embedding covers the whole document, not just the title:
`Program.cs` builds the text sent to the embedding model from
`{Title}\n{Authors}\n{Categories}\n{Description}` before storing it, so a paraphrase
of the description alone (as here) is enough to match.

## 4. Hybrid search blends both signals

`HybridSearchAsync` runs keyword and vector search independently and combines their
rankings with a Reciprocal Rank Fusion-style formula (`1/(60+rank)` summed per
result), rewarding books that rank well on *either* axis, with a small bonus for
books that rank well on *both*.

Query: `"wizards and heroes"`

```
--- Hybrid Search for "wizards and heroes" ---
  [Hybrid] The Name of the Wind (score: 0.0332)
  [Hybrid] Pride and Prejudice (score: 0.0167)
  [Hybrid] Dune (score: 0.0167)
  [Hybrid] The Midnight Library (score: 0.0167)
  [Hybrid] The Great Gatsby (score: 0.0167)
```

*The Name of the Wind* wins outright here — it topped both the keyword and vector
result sets individually, so its combined RRF score is roughly double that of books
that only appeared in the vector results. (The score scale itself isn't comparable
across strategies — `ts_rank_cd`, cosine distance, and RRF sums are different units —
only the *within-method* ranking is meaningful.)

## 5. Fuzzy search catches typos the other three miss

`FuzzySearchAsync` uses `pg_trgm`: it breaks `title` into overlapping 3-character
sequences ("trigrams") and matches on how many trigrams two strings share, via the
`%` operator and `similarity()`, backed by the `books_title_trgm_idx` GIN index. It
doesn't understand words or meaning at all — purely character-level overlap — which
makes it the odd one out, and complementary to the other three.

**Scope is narrower than keyword/vector search, on purpose here:** unlike
`search_document` (title+authors/categories+description, §1) and the embedding text
(title+authors+categories+description, §3), fuzzy search only has a trigram index
on `title` — there's no index on `description`. So a typo of a word that only
appears in the description won't match. For example, `"wizrd"` (a typo of "wizard")
returns nothing, even though *The Name of the Wind*'s description says "an infamous
wizard" — "wizard" isn't in that book's title, so there's nothing for the trigram
comparison to match against. Extending fuzzy search to the description would mean
adding a second `gin_trgm_ops` index on that column and checking similarity against
both.

Query: `"neuromancr"` (missing the final "e")

```
--- Keyword Search for "neuromancr" ---
  No results from Keyword search.

--- Vector Search for "neuromancr" ---
  [Vector] Neuromancer (score: 0.3300)
  [Vector] Brave New World (score: 0.1158)
  [Vector] Dune (score: 0.0697)
  [Vector] Project Hail Mary (score: 0.0632)
  [Vector] 1984 (score: 0.0141)

--- Hybrid Search for "neuromancr" ---
  [Hybrid] 1984 (score: 0.0167)
  [Hybrid] Project Hail Mary (score: 0.0167)
  [Hybrid] Dune (score: 0.0167)
  [Hybrid] Brave New World (score: 0.0166)
  [Hybrid] Neuromancer (score: 0.0166)

--- Fuzzy Search for "neuromancr" ---
  [Fuzzy] Neuromancer (score: 0.6429)
```

Keyword search finds nothing — the misspelled token doesn't stem to anything in the
corpus. Vector search does surface *Neuromancer*, but only because the embedding
model happens to encode the misspelled token close enough to the real word — it's
not designed for typo tolerance and the score is muted (0.33, versus 0.65+ for a
correctly-spelled semantic match elsewhere in this document). Hybrid search actually
makes things *worse* here: because keyword search contributes nothing, *Neuromancer*
gets diluted down to last place behind four unrelated books that only ranked
decently on the vector side. Fuzzy search is the only one of the three built for
this exact failure mode, and it shows: *Neuromancer* is the sole, high-confidence
result, purely from character overlap between "neuromancr" and "Neuromancer" — no
embeddings or linguistic parsing involved.

This is a real gap for AI agents too: user or tool-generated queries with typos,
OCR errors, or transliteration variants defeat FTS outright and only weakly survive
vector search. `pg_trgm` is cheap to add as a fallback or a blended signal alongside
the other two.

## 6. Graph search: how books are related, without touching text at query time

This is the least obvious one, and worth explaining in full because it doesn't work
the way you might expect a "book similarity graph" to work.

**Apache AGE stores node properties as `agtype`, which has no `pgvector` support** —
`agtype <=> agtype` doesn't exist as an operator. So the graph can't compute
similarity itself. Instead, `BuildGraphAsync`:

1. Creates a `(:Book)` node per book (plus per-book `Author`/`Category`/`Decade`
   nodes connected by `WROTE`/`IN_CATEGORY`/`PUBLISHED_IN` edges — these don't connect
   two different books to each other, since each book gets its own copies).
2. For each book, runs a plain SQL query against the relational `books` table —
   `ORDER BY embedding <=> embedding LIMIT 5` — to find its 5 nearest neighbors by
   embedding distance, entirely outside Cypher.
3. Writes each of those 5 as a `(book)-[:SIMILAR_TO]->(neighbor)` edge back into the
   graph.

`GraphSearchAsync` then does the one thing embeddings alone can't: **multi-hop
traversal**. It walks `SIMILAR_TO` edges 1 to 3 hops out from a seed book —
`MATCH (b:Book {id: $seed})-[:SIMILAR_TO*1..3]->(related:Book)` — surfacing books
that are similar *to a book that's similar to the seed*, not just the seed's own
direct top-5.

Direct nearest neighbors of `1984` (from step 2, straight `pgvector` distance):

| Neighbor | Distance |
|---|---|
| Brave New World | 0.5719 |
| The Midnight Library | 0.7750 |
| Pride and Prejudice | 0.7798 |
| Dune | 0.7947 |
| Neuromancer | 0.7975 |

Graph search from the same seed (`*1..3` hops):

```
--- Graph Search ---
  [Project Hail Mary] Project Hail Mary
  [Dune] Dune
  [The Midnight Library] The Midnight Library
  [Neuromancer] Neuromancer
  [Brave New World] Brave New World
```

*Project Hail Mary* shows up here even though it's **not** in `1984`'s direct top-5
neighbor list above. It's reachable in 2 hops: `1984 → Neuromancer → Project Hail
Mary` (`Neuromancer`'s own nearest-neighbor query independently ranks *Project Hail
Mary* highly). That's what graph traversal buys you over a flat vector search: it
follows chains of similarity the single-book nearest-neighbor query can't see, at the
cost of the edges being a stale snapshot from whenever the graph was last built,
rather than a live similarity computation.

**Worth knowing if you extend this**: baking embedding similarity into fixed graph
edges like this is a demo simplification, not a general recommendation — in a real
system you'd more commonly keep nearest-neighbor lookups as a live `pgvector` query
and reserve graph edges for relationships that are genuinely relational (shared
author, citation, co-purchase), rather than a distance ranking you could recompute
on demand.

### Viewing the graph

You can inspect the `SIMILAR_TO` graph directly in `psql` (or any Postgres client)
with a Cypher query run through AGE's `cypher()` function:

```sql
LOAD 'age';
SET search_path = ag_catalog, "$user", public;

SELECT *
FROM cypher('books_graph', $$
    MATCH (source)-[edge]->(target)
    RETURN source, edge, target
    LIMIT 200
$$) AS graph_data(
    source agtype,
    edge agtype,
    target agtype
);
```

A client with graph visualization support can render the resulting rows as a
node/edge diagram, like the screenshot below. The screenshot was captured using
the [PostgreSQL extension by Microsoft](https://marketplace.visualstudio.com/items?itemName=ms-ossdata.vscode-pgsql)
in VS Code: connect it to the `books` database, run the query above in a new
query editor, then switch the results view to its graph visualization to see the
nodes and edges laid out interactively.

<img width="827" height="629" alt="image" src="https://github.com/user-attachments/assets/1fa021f4-6869-4ecd-b6e1-cd7be82d10a9" />


## Running these yourself

Before starting the container, set your OpenRouter API key in
`app/appsettings.json` (or `app/appsettings.Development.json`) — the app reads
it on startup and will error out immediately if it's missing:

```json
{
  "OPENROUTER_API_KEY": "sk-or-v1-..."
}
```

Then bring up the database and run the app:

```powershell
docker compose up -d --build
cd app
dotnet run
```

Answer `y` the first time to seed data, generate embeddings, and build the graph.
On later runs, answer `n` to reuse what's already in the database. The app then
loops, asking for a new search phrase each time — try your own paraphrases (and
typos) against the descriptions above and compare all five result sets. Type
`exit` to quit.
