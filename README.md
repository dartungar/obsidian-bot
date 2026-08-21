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

## Agent capture API

`/v1` implements the review-first capture loop in [agent-capture.md](agent-capture.md):
an agent may search, read, and create immutable proposals; a separate reviewer
credential approves an exact server-generated preview; an internal publisher is
the only component that writes to the vault.

The machine-readable contract is publicly available at
`GET /openapi/v1.json`. It contains every `/v1` route, request and response
schema, bearer-token setup, and operation descriptions; no token is included in
the document. Agents should retrieve it before their first API call.

Configure distinct strong tokens and explicit writable folders:

```env
OBSIDIAN_AGENT_API_TOKEN=...
OBSIDIAN_REVIEW_API_TOKEN=...
OBSIDIAN_AGENT_WRITABLE_FOLDERS=_inbox,01 projects
# Optional: limit agent reads too. An empty value permits all non-denied folders.
OBSIDIAN_AGENT_READABLE_FOLDERS=01 projects,_inbox
```

The Compose deployment runs three roles from the same image:

- `obsidian-agent-api` exposes `/v1` and receives `/var/notes` read-only.
- `obsidian-publisher` has the read/write vault mount, no host port, and applies
  only approved proposals.
- `obsidian-bot` retains the Telegram workflow but has no host-published API port
  in the supplied Compose file.

The proposal database and reversible JSON snapshots live in the shared
`obsidian-bot-data` volume, outside the vault. The API refuses to expose
`.obsidian`, attachments, templates, archive folders, and configured denied paths.
New-note and append destinations must be in `OBSIDIAN_AGENT_WRITABLE_FOLDERS`;
the conservative default is `_inbox` only.

Agent-token scopes are `notes:read`, `proposals:create`, and `proposals:read`.
Reviewer tokens have `proposals:review`, `proposals:read`, and `audit:read`.
The agent token cannot call the review endpoint or publish a change.

Typical existing-note flow:

```bash
# 1. Search, then retrieve the candidate's headings/section.
curl --get http://localhost:8080/v1/notes \
  --header "Authorization: Bearer $OBSIDIAN_AGENT_API_TOKEN" \
  --data-urlencode 'q=checkout routing' \
  --data-urlencode 'mode=hybrid' \
  --data-urlencode 'include=headings,snippet'

# 2. Create an immutable, server-previewed append proposal.
curl --request POST http://localhost:8080/v1/change-proposals \
  --header "Authorization: Bearer $OBSIDIAN_AGENT_API_TOKEN" \
  --header 'Idempotency-Key: 7825a1b1-...' \
  --header 'Content-Type: application/json' \
  --data '{
    "type":"append_section",
    "target":{"noteId":"note_...","baseRevision":"sha256:...","sectionId":"section_..."},
    "contentMarkdown":"- 2026-08-19 — Decision text.",
    "rationale":"This belongs in the project Decisions section."
  }'

# 3. A human approves precisely the returned preview hash.
curl --request POST http://localhost:8080/v1/change-proposals/proposal_.../reviews \
  --header "Authorization: Bearer $OBSIDIAN_REVIEW_API_TOKEN" \
  --header 'Content-Type: application/json' \
  --data '{"decision":"approved","approvedPreviewHash":"sha256:..."}'
```

The publisher re-reads the note and its opaque section, checks the original
revision, creates a snapshot, and writes atomically. Any intervening change
transitions the proposal to `conflicted`; it is never rebased or overwritten.
Use `GET /v1/change-proposals/{proposal_id}/publication` to observe the outcome
and `GET /v1/audit-events?proposal_id={proposal_id}` with the reviewer token to
inspect its audit trail.
