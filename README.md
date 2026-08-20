# Obsidian Bot

Telegram bot for capturing notes, tasks, voice notes, and images into an Obsidian vault.

## Search

- `/search <query>` combines local full-text and semantic results in one response.
- `/semantic <query>` runs semantic search only using embeddings and `sqlite-vec`.

The bot keeps its local SQLite index at `.obsidian-bot/search.db` by default. It builds the index at startup and watches Markdown files for changes, creating fresh embeddings shortly after a note is created, changed, renamed, or deleted. A full reconciliation runs every 60 seconds by default to cover missed filesystem events. The index is safe to delete; it will be rebuilt automatically.

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
