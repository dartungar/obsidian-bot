# Obsidian Bot

Telegram bot for capturing notes, tasks, voice notes, and images into an Obsidian vault.

## Search

- `/search <query>` combines local full-text and semantic results in one response.
- `/semantic <query>` runs semantic search only using embeddings and `sqlite-vec`.

The bot keeps its local SQLite index at `.obsidian-bot/search.db` by default. It builds the index at startup and watches Markdown files for changes, creating fresh embeddings shortly after a note is created, changed, renamed, or deleted. A full reconciliation runs every 60 seconds by default to cover missed filesystem events. The index is safe to delete; it will be rebuilt automatically.

Search replies use Telegram message entities: note paths are underlined, and common Markdown formatting is preserved in snippets. YAML frontmatter is hidden only in the Telegram response; it remains available to the search index and future API use.

Full-text search works with no external service. Semantic search requires an OpenAI embeddings API key:

```env
OPENAI_API_KEY=...
```

The default semantic model is `text-embedding-3-small` with 1,536 dimensions. These settings can be adjusted for an OpenAI-compatible embeddings endpoint:

```env
OPENAI_EMBEDDINGS_URL=https://api.openai.com/v1/embeddings
OPENAI_EMBEDDING_MODEL=text-embedding-3-small
OPENAI_EMBEDDING_DIMENSIONS=1536
OBSIDIAN_SEARCH_DATABASE_PATH=.obsidian-bot/search.db
OBSIDIAN_SEARCH_RESULT_LIMIT=5
OBSIDIAN_SEARCH_RECONCILE_INTERVAL_SECONDS=60
```

Changing the embedding model or dimension count clears only stored vectors; they are regenerated on the next semantic search.

## API

The HTTP API is protected by a bearer token. Set a strong value before exposing the
container:

```env
OBSIDIAN_API_TOKEN=...
OBSIDIAN_API_PORT=8080
```

With Docker Compose, the API listens on `http://localhost:8080` by default. Every
`/api` endpoint requires `Authorization: Bearer <OBSIDIAN_API_TOKEN>`; `/healthz`
is intentionally unauthenticated for container health checks. The default Docker
binding is loopback-only. To expose it through a TLS-terminating reverse proxy,
set `OBSIDIAN_API_BIND_ADDRESS=0.0.0.0`; do not expose the bearer token over plain
HTTP on an untrusted network.

The API exposes the same commands as Telegram at `POST /api/commands/{command}`:

- `add` saves text. Its JSON body requires `content` and `destination`. Capture
  destinations are `today`, `yesterday`, `inbox`, or `date` (with `date` as
  `YYYY-MM-DD`). Set `asTask` to `true` to create a task; task destinations are
  `today`, `tomorrow`, and `inbox`.
- `search` accepts `query` and returns the combined full-text and semantic results.
- `semantic` accepts `query` and returns semantic results only.
- `cancel` is accepted for command parity and returns success. API calls are
  stateless, so there is no pending server-side capture to clear.

For example:

```bash
curl --request POST http://localhost:8080/api/commands/add \
  --header "Authorization: Bearer $OBSIDIAN_API_TOKEN" \
  --header "Content-Type: application/json" \
  --data '{"content":"Plan the release","destination":"today"}'

curl --request POST http://localhost:8080/api/commands/search \
  --header "Authorization: Bearer $OBSIDIAN_API_TOKEN" \
  --header "Content-Type: application/json" \
  --data '{"query":"release plan"}'
```
