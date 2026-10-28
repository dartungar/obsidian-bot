# Obsidian Bot

Telegram bot for capturing notes, tasks, voice notes, and images into an Obsidian vault.

## Managed Obsidian Sync

The app can initialize a local vault, link it to Obsidian Sync, and supervise
timed sync rounds. The Linux Compose deployment adds an `obsidian-sync` service
with the official `obsidian-headless` client pinned to **0.0.14**. Existing
deployments keep their current behavior until `OBSIDIAN_SYNC_ENABLED=true`.
An active Obsidian Sync subscription is required.

### Coolify setup

Deploy this repository's Compose file through Coolify and set these runtime
environment variables (keep passwords as secrets, unavailable during builds):

```env
OBSIDIAN_SYNC_ENABLED=true
OBSIDIAN_ACCOUNT_EMAIL=your-obsidian-account-email
OBSIDIAN_ACCOUNT_PASSWORD=your-obsidian-account-password
OBSIDIAN_SYNC_VAULT=your-vault-name-or-id
OBSIDIAN_SYNC_PASSWORD=your-vault-encryption-password
```

Fill in the existing Telegram/API settings too. A unique vault name works, so
you do not need to retrieve an ID or auth token manually. On startup the app
uses the official headless client to log in, saves its session, configures the
vault, and starts sync. Leave `OBSIDIAN_AUTH_TOKEN` empty for automatic login.
An explicitly supplied token takes precedence; an invalid explicit token must
be replaced or removed before account-based login will be attempted.

If your account uses MFA, let the first deployment finish building. When
`/syncstatus` reports `SYNC_MFA_REQUIRED`, set `OBSIDIAN_ACCOUNT_MFA_CODE` to a
fresh authenticator code in Coolify and restart `obsidian-sync` without another
build. After successful login, clear this one-time code. Valid saved sessions
are reused across restarts, so MFA is needed again only when authentication
expires. Expired cached sessions are renewed automatically when the configured
credentials suffice. Login failures retry slowly and appear in sync status;
they do not crash the container or expose account credentials in logs.

Stop any other sync client operating on the server's `/var/obsidian` directory.
Desktop/mobile clients on other devices continue syncing normally. For an
existing, nonempty `/var/obsidian`, set `OBSIDIAN_SYNC_ADOPT_EXISTING=true`.
Before its first managed sync, the app creates an `initial-vault-backup-*.zip`
in the shared sync state directory. Symbolic links are refused during adoption.
The backup does not expire automatically; retain it until migration is verified.

Account sessions and headless sync metadata persist in the private
`obsidian-headless-data` volume. They are mounted only into `obsidian-sync`.
Only the sync service receives the account credentials, MFA code, authentication
token, and vault encryption password. Account and vault passwords are separate.
The derived encryption key persists after setup, so the vault password is not
required for subsequent rounds using that configuration. Keep both persistent
volumes and the vault together when backing up or migrating. For local Compose
use, copy `.env.example` to `.env`, fill in the same settings, and start the stack.

To create a remote vault automatically, set `OBSIDIAN_SYNC_CREATE_REMOTE=true`
and use its desired **name** as `OBSIDIAN_SYNC_VAULT`. Creation uses end-to-end
encryption and requires the encryption password. Existing matching vaults are
reused; ambiguous names are refused. Creation is never attempted after a saved
remote identity disappears.

For container secrets, set `OBSIDIAN_ACCOUNT_PASSWORD_FILE`,
`OBSIDIAN_ACCOUNT_MFA_CODE_FILE`, `OBSIDIAN_AUTH_TOKEN_FILE`, and/or
`OBSIDIAN_SYNC_PASSWORD_FILE` to mounted secret paths. File values override
environment values and are read again when commands run. These paths must be
mounted into the sync container, for example with a Compose override:

```yaml
services:
  obsidian-sync:
    secrets:
      - obsidian-auth-token
      - obsidian-sync-password
    environment:
      OBSIDIAN_AUTH_TOKEN_FILE: /run/secrets/obsidian-auth-token
      OBSIDIAN_SYNC_PASSWORD_FILE: /run/secrets/obsidian-sync-password
secrets:
  obsidian-auth-token:
    file: /private/obsidian-auth-token
  obsidian-sync-password:
    file: /private/obsidian-sync-password
```

### Runtime behavior

- A round runs **25 seconds after the previous round finishes**. There is no
  overlap. Failed rounds back off exponentially; credential/setup failures retry
  more slowly so an unattended service does not hammer authentication.
- Normal rounds time out after 180 seconds; initial setup/download has a
  1,800-second allowance. A timeout or application shutdown kills the child
  process tree. Success requires exit code zero, an exact `Fully synced` event,
  and no reported stderr errors. Raw client output and secret-bearing arguments
  are never written into application logs or status responses.
- Sync is bidirectional. Conflicting edits use the client's `conflict` strategy
  to preserve conflicting copies. Image, audio, video, and PDF attachments sync;
  Obsidian application/plugin configuration and unsupported file types do not.
  `.obsidian-bot` and `.git` are excluded.
- Bot/API writes and sync rounds share a cross-process vault lock. Writes wait
  while a round runs, so a large round can delay a capture. Per-file lock files
  live outside the vault when managed sync is enabled.
- Note reads, writes, publishing, and indexing wait until the initial sync
  completes. Configured, permitted agent writable folders are created during
  initialization so an empty remote vault can receive its first capture.
  Once initialized, local captures remain available during network
  outages. A saved local/remote identity prevents automatic relinking. A small,
  excluded `.obsidian-bot/sync-instance` marker detects lost or replaced vault
  volumes before retained sync metadata could propagate unintended deletions.
- The search index watches downloaded Markdown changes and reconciles missed
  filesystem events. Search databases and snapshots stay outside the vault.

### Health and recovery

- Telegram `/syncstatus` shows state, local availability, last successful sync,
  and failure count. The allowed Telegram user receives one notification per
  unhealthy incident and one on recovery. Incidents include three consecutive
  failures, credentials/setup needing attention, or stale sync progress.
- Authenticated `GET /v1/sync/status` exposes the same information plus attempt
  times, duration, next retry, and error code. Both agent and reviewer tokens
  have `sync:read`; credentials are never returned.
- `/healthz` checks application liveness. `/readyz` returns 503 until the local
  vault is initialized, then stays ready during temporary sync outages.
  `/sync-healthz` returns 503 for an unhealthy or stale sync service. Anonymous
  health endpoints expose only a coarse state. Docker uses `/sync-healthz` for
  the sync service and liveness for the other services; failed sync rounds are
  recovered by the supervisor rather than relying on Docker health restarts.
- Successful rounds establish synchronization with the server. They do not
  prove another device has downloaded a change; verify both directions with a
  second client before migrating the real vault.

| Setting | Default |
| --- | --- |
| `OBSIDIAN_SYNC_INTERVAL_SECONDS` | `25` |
| `OBSIDIAN_SYNC_TIMEOUT_SECONDS` | `180` |
| `OBSIDIAN_SYNC_INITIAL_TIMEOUT_SECONDS` | `1800` |
| `OBSIDIAN_SYNC_STALE_AFTER_SECONDS` | `300` |
| `OBSIDIAN_SYNC_MAX_BACKOFF_SECONDS` | `300` |
| `OBSIDIAN_SYNC_ATTENTION_RETRY_SECONDS` | `900` |
| `OBSIDIAN_SYNC_DEVICE_NAME` | `obsidian-bot` |
| `OBSIDIAN_SYNC_REGION` | automatic; used only for remote creation |

After a login failure, update account credentials/MFA or replace an explicit
token in Coolify, then restart `obsidian-sync` to retry immediately. For an identity
mismatch or missing vault marker, restore the matching vault and state volumes;
do not clear headless metadata or relink automatically. For an intentional move
to a different remote vault, stop the services and initialize a separate local
vault with fresh sync state, then migrate notes explicitly.

Empty storage-path variables use defaults. Validation checks only the storage
used by each component role; a sync-only container does not open or validate
search/proposal databases. If an active setting points inside the vault, the
startup error names that variable and its resolved path. Keep shared sync
state under `/var/lib/obsidian-bot/sync`, private headless state under
`/var/lib/obsidian-headless`, and search/proposal databases and snapshots under
`/var/lib/obsidian-bot`, outside the `/var/notes` vault mount.

### Verification

```bash
dotnet build ObsidianBot.csproj
python3 -m unittest discover -s tests -v
docker compose config --quiet
```

The integration suite uses temporary vaults, real application processes and
HTTP requests, and a fake headless executable. It covers initialization,
adoption backups, remote creation, restart persistence, authentication and stale
health, automatic login and MFA, cached-session renewal, storage-path validation,
incomplete rounds, timeouts and child termination, owner exclusivity, and
coordination with API writes. It never connects to Obsidian or Telegram.

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
