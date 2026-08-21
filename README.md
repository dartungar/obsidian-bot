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

## Agent vault API

`/v1` is a controlled editor for the real vault. It exposes search and read
operations plus narrowly scoped, revision-checked direct changes:
`create_note`, `append_section`, and `append_task`. Each successful change is
written atomically, audited, snapshotted, and undoable by the same agent during
the configured undo window. The API never accepts arbitrary filesystem paths,
whole-note replacements, moves, or deletes.

The existing proposal/review flow remains available at `/v1/change-proposals`
for Tier 2 work such as replacement, frontmatter changes, or broad edits. The
agent token cannot approve proposals.

Retrieve `GET /openapi/v1.json` before the first API call and use
`GET /v1/capabilities` to discover the server-enforced direct-edit policy.
Search and note responses advertise which direct operations and section IDs are
currently eligible.

Configure strong, separate tokens and the server-side policy:

```env
OBSIDIAN_AGENT_API_TOKEN=...
OBSIDIAN_REVIEW_API_TOKEN=...
OBSIDIAN_AGENT_WRITABLE_FOLDERS=_inbox,01 projects
# Optional: limit agent reads too. An empty value permits all non-denied folders.
OBSIDIAN_AGENT_READABLE_FOLDERS=01 projects,_inbox
OBSIDIAN_AGENT_DIRECT_ALLOWED_HEADINGS=Notes,Decisions,Tasks,Next Steps,Journal,Agent Capture
OBSIDIAN_DIRECT_CHANGE_MAX_CONTENT_BYTES=25600
OBSIDIAN_DIRECT_CHANGE_UNDO_WINDOW_SECONDS=86400
```

Protected paths are denied regardless of agent input: `.obsidian`, `.git`,
`attachments`, `templates`, and `04 archive` (plus any configured denied path).
The agent token is scoped to `notes:read`, `notes:create`,
`notes:append-section`, `notes:append-task`, `changes:read`, and
`changes:undo-own`, as well as the legacy proposal-create/read scopes.

The Compose deployment runs three roles from the same image:

- `obsidian-agent-api` exposes `/v1`, has the writable vault mount, and enforces
  direct-edit policy, locking, snapshots, and atomic writes.
- `obsidian-publisher` has a writable vault mount, no host port, and applies only
  approved legacy proposals.
- `obsidian-bot` retains the Telegram workflow and has no host-published API port
  in the supplied Compose file.

The change database and snapshots reside in the shared `obsidian-bot-data`
volume outside the vault.

Typical direct append:

```bash
# 1. Search or read the note to obtain opaque note/section IDs and its revision.
curl --get http://localhost:8080/v1/notes \
  --header "Authorization: Bearer $OBSIDIAN_AGENT_API_TOKEN" \
  --data-urlencode 'q=checkout routing' \
  --data-urlencode 'mode=hybrid' \
  --data-urlencode 'include=headings,snippet'

# 2. Apply one allowed, additive edit.
curl --request POST http://localhost:8080/v1/note-changes \
  --header "Authorization: Bearer $OBSIDIAN_AGENT_API_TOKEN" \
  --header 'Idempotency-Key: 7825a1b1-...' \
  --header 'Content-Type: application/json' \
  --data '{
    "operation":"append_section",
    "noteId":"note_...",
    "sectionId":"section_...",
    "baseRevision":"sha256:...",
    "contentMarkdown":"- 2026-08-21 — Decision text.",
    "rationale":"This is the named project note and its Decisions section is allowed.",
    "origin":{"conversationId":"chat_...","requestExcerpt":"Add the decision."}
  }'

# 3. Undo only after an explicit user request.
curl --request POST http://localhost:8080/v1/changes/change_.../undo \
  --header "Authorization: Bearer $OBSIDIAN_AGENT_API_TOKEN"
```

A stale revision returns `409 REVISION_CONFLICT` without changing the file. A
repeat with the same idempotency key returns the original direct-change result;
reusing that key for different content returns `409 IDEMPOTENCY_KEY_REUSED`.
