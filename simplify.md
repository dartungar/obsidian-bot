# Obsidian Direct-Edit API Specification

**Status:** Proposed companion specification
**Version:** v0.2-draft
**Created:** 2026-08-21
**Companion to:** `obsidian-capture-api-spec.md` (v0.1 proposal/review workflow; unchanged)

---

## 1. Purpose

Provide an agent with useful, safe interaction with the real Obsidian vault: search, read, create notes, and make narrowly defined edits to existing notes.

This specification replaces the **proposal → review → publisher** sequence for routine, low-risk edits with a single, transactional direct-edit operation. It does **not** give the agent generic filesystem write access or unrestricted document editing.

The desired normal interaction is:

> **User:** “Today we decided we will roll out Unlimit to Poland.”
> **Agent:** Finds `[[Unlimit Payments]]`, appends a dated decision to `## Decisions` or `## Notes`, and reports the applied change with an Undo option.

The user should only be interrupted for ambiguity, conflicts, or higher-risk mutations.

---

## 2. Goals

1. Let the agent work with the actual vault, including existing project, daily, and area notes.
2. Make normal user-directed edits one agent operation rather than a multi-stage approval workflow.
3. Prevent raw filesystem access from the agent runtime.
4. Make every write revision-checked, atomic, auditable, and reversible.
5. Keep protected areas and destructive operations unavailable by default.
6. Preserve the v0.1 proposal/review workflow for exceptional, broad, or destructive work.

## 3. Non-goals

This version does not support:

- arbitrary whole-note replacement;
- deletes, moves, or renames;
- attachment creation or binary writes;
- writing under `.obsidian/`, templates, archives, or other protected paths;
- shell execution or arbitrary filesystem paths;
- bulk edits across many notes;
- automatic conflict merging.

---

## 4. Security and Process Boundary

```text
Hermes agent ── scoped HTTPS API ── transaction/backup layer ── synced vault
                    │
                    └── audit log + change IDs + undo records
```

### 4.1 Agent boundary

- Hermes receives **one agent token**.
- Hermes never receives a writable vault mount or a generic filesystem API.
- The API service has the writable mount and enforces path, operation, revision, and size policies before it writes.
- The API service performs the snapshot and atomic write itself.

### 4.2 Credentials

The standard agent identity has only these scopes:

```text
notes:read
notes:create
notes:append-section
notes:append-task
changes:read
changes:undo-own
```

The agent must **not** receive the v0.1 reviewer credential in its runtime.

A separate reviewer identity may remain available to a human review UI for Tier 2 changes (Section 6). It is not part of the ordinary chat path.

### 4.3 Protected paths

The API must reject mutations to these paths regardless of caller input:

```text
.obsidian/**
attachments/**
templates/**
04 archive/**
**/.git/**
```

The actual protected-path policy must be configured server-side, not supplied by the agent in a request.

---

## 5. Direct-Edit Safety Model

A direct edit is permitted only when all of the following are true:

1. The operation is in the direct-edit allowlist.
2. The target path is allowed by server policy.
3. The target heading/section is allowed for that operation.
4. The request contains the current note revision.
5. The note still has that revision when the transaction begins.
6. The content obeys size and Markdown-validation limits.
7. The service can create a rollback snapshot before modifying the vault.

If any check fails, the service returns a structured failure and writes nothing.

### 5.1 Direct-edit allowlist

| Operation | Directly applied? | Notes |
|---|---:|---|
| `create_note` | Yes | Only in API-advertised folders; create-only and collision-rejecting |
| `append_section` | Yes | Only to an existing, permitted named section |
| `append_task` | Yes | Specialized append to a permitted task section |
| `replace_section` | No, Tier 2 | May remove existing information |
| `set_frontmatter` | No, Tier 2 | May affect workflows/plugins |
| `move_note` / `rename_note` | No | Not implemented in v0.2 |
| `delete_note` | No | Not implemented in v0.2 |

### 5.2 Allowed sections

The server must resolve sections by stable section ID and heading path, never line number. Initial default headings:

```text
Notes
Decisions
Tasks
Next Steps
Journal
Agent Capture
```

The allowed headings are a server-side policy. A note can be readable but not appendable; the API must report this explicitly.

### 5.3 Content restrictions

For direct operations, enforce at least:

- Markdown-only content;
- maximum 25 KiB added content per request;
- no frontmatter modification in append operations;
- no attempt to close or alter unrelated Markdown sections;
- explicit source conversation metadata in the audit record;
- optional rate limit, e.g. 30 direct changes per agent per hour.

---

## 6. Two Operation Tiers

### Tier 1 — direct, reversible edits

Use automatically for clear user-directed actions such as:

- “Add this decision to `[[Unlimit Payments]]`.”
- “Put this task under today’s Tasks section.”
- “Create a project note for X.”
- “Add this meeting note under Notes.”

The agent may search and read several notes internally, but should make one final mutation and then report the result.

### Tier 2 — proposal/review edits

Retain the existing v0.1 proposal API for operations that are destructive, broad, uncertain, or semantically risky:

- replacing existing section content;
- rewriting frontmatter;
- editing several notes;
- archiving, moving, renaming, or deleting;
- applying a change after an unresolvable revision conflict.

Tier 2 remains:

```text
proposal → explicit human review → publisher → final verification
```

The key change is that Tier 2 becomes the exception rather than the default.

### 6.1 Deterministic risk routing

The agent must not decide on its own to bypass review. The API policy is the final authority and classifies every requested mutation into one of three outcomes:

| Outcome | Conditions | Result |
|---|---|---|
| **Direct** | One target note; operation is in the direct allowlist; stable allowed section; content is additive; size is within limit; revision matches | Apply atomically through `POST /v1/note-changes` |
| **Clarify** | Two or more plausible targets, section intent is unclear, or the user’s requested content is underspecified | Write nothing; ask the user one focused selection question |
| **Review required** | The action may remove/replace existing content, alter frontmatter, affect more than one note, or otherwise falls outside the direct allowlist but is not forbidden | Create an immutable v0.1 proposal and request human approval |
| **Denied** | Protected path, filesystem-like operation, deletion, archive/config/attachment modification, or invalid request | Reject; no proposal and no write |

The agent may use `GET /v1/capabilities` and per-note policy metadata to select the correct route in advance. The API must still enforce the classification when it receives the request; a caller cannot set a `tier` field to override policy.

Examples:

| User intent | Classification |
|---|---|
| “Add this decision under `## Notes` in `[[Unlimit Payments]]`.” | Direct, if that note and section are permitted |
| “Add this to one of my payment project notes.” | Clarify; ask the user to choose a target before creating any proposal |
| “Replace the old rollout plan with this new one.” | Review required; it may remove or rewrite content |
| “Change this project’s status frontmatter.” | Review required |
| “Apply these updates to five project notes.” | Review required |
| “Delete the old Unlimit note.” | Denied in v0.2 |
| “Edit `.obsidian/app.json`.” | Denied in v0.2 |

### 6.2 Human approval flow for Tier 2

Tier 2 is deliberately not an ordinary chat round-trip. The agent has no reviewer credential, so it cannot approve its own proposal.

1. The agent creates a v0.1 immutable change proposal using its agent token.
2. The API returns the proposal ID, exact server-generated diff, preview hash, expiry, and a `reviewUrl` for the authenticated human review UI.
3. The agent presents a concise summary and the review link, for example: “This replaces 18 lines in `[[Unlimit Payments]]`; review and approve it here.”
4. The user authenticates to the review UI and clicks **Approve** or **Reject**. The UI holds the reviewer identity; Hermes does not.
5. The publisher validates the proposal revision, writes a snapshot, applies or conflicts, and records the final audit event.
6. The agent reads the proposal/publication status and reports the final result.

If chat-native approval is required later, use a platform-native, user-originated approval control that sends a signed approval event directly to the review service. Do not reintroduce the reviewer secret into the Hermes runtime merely to support the word “approve” in chat.

A reviewable request may return this structured response instead of applying directly:

```http
HTTP/1.1 422 Unprocessable Content
```

```json
{
  "code": "REVIEW_REQUIRED",
  "message": "This operation can modify existing content beyond the direct-edit policy.",
  "requestedOperation": "replace_section",
  "reason": "Existing section content would be replaced.",
  "nextAction": "create_change_proposal"
}
```

---

## 7. Capability Discovery

### 7.1 `GET /v1/capabilities`

Return the current policy to an authenticated agent. This allows the client to make correct choices without attempting edits that will fail.

```http
GET /v1/capabilities
Authorization: Bearer $OBSIDIAN_AGENT_API_TOKEN
```

```json
{
  "apiVersion": "v0.2",
  "directOperations": [
    "create_note",
    "append_section",
    "append_task"
  ],
  "allowedHeadingPaths": [
    ["Notes"],
    ["Decisions"],
    ["Tasks"],
    ["Next Steps"],
    ["Journal"],
    ["Agent Capture"]
  ],
  "writableFolders": [
    {
      "id": "folder_projects",
      "path": "01 projects"
    },
    {
      "id": "folder_inbox",
      "path": "_inbox"
    }
  ],
  "protectedPathPrefixes": [
    ".obsidian",
    "attachments",
    "templates",
    "04 archive"
  ],
  "maxDirectContentBytes": 25600,
  "undoWindowSeconds": 86400
}
```

### 7.2 Enrich `GET /v1/notes`

Each search candidate should advertise policy rather than forcing the agent to discover writability through a rejected write.

```json
{
  "id": "note_4ef26f97f425c718f142268a",
  "path": "01 projects/Unlimit Payments.md",
  "title": "Unlimit Payments",
  "revision": "sha256:...",
  "policy": {
    "readable": true,
    "directOperations": ["append_section"],
    "allowedSectionIds": ["section_notes", "section_decisions"],
    "requiresReviewFor": ["replace_section", "set_frontmatter"]
  }
}
```

A policy rejection must appear in this response before a mutation is attempted whenever possible.

---

## 8. Direct Change API

### 8.1 `POST /v1/note-changes`

Apply one direct, allowable mutation atomically.

```http
POST /v1/note-changes
Authorization: Bearer $OBSIDIAN_AGENT_API_TOKEN
Content-Type: application/json
Idempotency-Key: <UUID>
```

Common request fields:

| Field | Required | Description |
|---|---:|---|
| `operation` | Yes | `create_note`, `append_section`, or `append_task` |
| `origin` | Yes | Source conversation metadata for auditability |
| `rationale` | Yes | Short explanation of target selection |
| `dryRun` | No | When true, validate and return a preview but do not write |

The endpoint accepts a tagged union based on `operation`.

#### Append to an existing section

```json
{
  "operation": "append_section",
  "noteId": "note_4ef26f97f425c718f142268a",
  "sectionId": "section_e9131c8cfe8174e27571be8d",
  "baseRevision": "sha256:31d510b48498400f58143ca0aa810ef6181d19ff40b0508bf5f396bc5fee5df2",
  "contentMarkdown": "- 2026-08-21 — Decision: roll out Unlimit to Poland.",
  "rationale": "The user named Unlimit; this is the matching active project note and Notes is an allowed capture section.",
  "origin": {
    "conversationId": "chat_...",
    "requestExcerpt": "Today we decided that we will roll out Unlimit to Poland."
  }
}
```

#### Append a task

```json
{
  "operation": "append_task",
  "noteId": "note_...",
  "sectionId": "section_tasks",
  "baseRevision": "sha256:...",
  "taskMarkdown": "- [ ] Confirm Poland rollout owner and timeline",
  "rationale": "The task belongs to the requested project’s Tasks section.",
  "origin": {
    "conversationId": "chat_...",
    "requestExcerpt": "Add a follow-up task for the Poland rollout."
  }
}
```

#### Create a note

```json
{
  "operation": "create_note",
  "folderId": "folder_projects",
  "filename": "Unlimit rollout to Poland.md",
  "onConflict": "reject",
  "frontmatter": {
    "type": "decision",
    "created_by": "agent"
  },
  "contentMarkdown": "# Unlimit rollout to Poland\n\n## Decision\n\n- 2026-08-21 — We decided to roll out Unlimit to Poland.",
  "rationale": "No suitable existing writable project note was found.",
  "origin": {
    "conversationId": "chat_...",
    "requestExcerpt": "Today we decided that we will roll out Unlimit to Poland."
  }
}
```

The server must derive the final vault path from `folderId` and a normalized filename. It must never accept an arbitrary absolute or relative file path.

### 8.2 Successful response

```http
HTTP/1.1 201 Created
```

```json
{
  "changeId": "change_01d65cae647a95e2909bdf72f1c3973a",
  "status": "applied",
  "operation": "append_section",
  "path": "01 projects/Unlimit Payments.md",
  "section": {
    "id": "section_e9131c8cfe8174e27571be8d",
    "headingPath": ["Notes"]
  },
  "snapshotId": "snap_01d65cae647a95e2909bdf72f1c3973a",
  "beforeRevision": "sha256:...",
  "afterRevision": "sha256:...",
  "unifiedDiff": "...",
  "undo": {
    "available": true,
    "expiresAt": "2026-08-22T09:17:04Z"
  }
}
```

A `dryRun: true` response returns the same targeting, validation, and diff information with:

```json
{
  "status": "validated",
  "changeId": null,
  "snapshotId": null
}
```

### 8.3 Idempotency

`Idempotency-Key` is required for every direct mutation.

- Retrying the same request with the same key returns the original result.
- Reusing a key with a materially different request returns `409 IDEMPOTENCY_KEY_REUSED`.
- The service stores the idempotency record alongside the change/audit record.

---

## 9. Transaction and Conflict Semantics

For an existing-note operation, the API must:

1. acquire a per-note transaction lock;
2. re-read the note from disk;
3. hash it and compare with `baseRevision`;
4. resolve the requested stable section ID and verify policy;
5. create a snapshot before mutation;
6. apply exactly the operation’s defined Markdown transformation;
7. write atomically through a temporary file and rename;
8. calculate the resulting revision and unified diff;
9. write an immutable audit event; and
10. release the lock.

If the current revision differs from `baseRevision`, return:

```http
HTTP/1.1 409 Conflict
```

```json
{
  "code": "REVISION_CONFLICT",
  "message": "The note changed after it was read; no write was applied.",
  "noteId": "note_...",
  "expectedRevision": "sha256:...",
  "currentRevision": "sha256:...",
  "recommendedAction": "read_and_retry"
}
```

Never merge or overwrite automatically on a conflict.

---

## 10. Undo

### 10.1 `POST /v1/changes/{changeId}/undo`

Undo one direct change after an explicit user request such as “undo that” or “revert the Unlimit decision.”

```http
POST /v1/changes/change_.../undo
Authorization: Bearer $OBSIDIAN_AGENT_API_TOKEN
```

The service may undo only if:

1. the requesting identity created the change;
2. the change is within the configured undo window;
3. the note is still at the `afterRevision` created by that change; and
4. no later conflicting change has been applied.

A successful undo is a new audited transaction with its own change ID and snapshot. If the note changed later, return `409 UNDO_CONFLICT` and require a Tier 2 review or an explicit fresh edit.

The agent must never automatically undo without a clear user instruction.

---

## 11. Errors

All errors must be JSON with a stable machine-readable `code` and useful remediation data.

| Status | Code | Meaning |
|---:|---|---|
| 400 | `INVALID_REQUEST` | Missing or malformed request fields |
| 401 | `AUTHENTICATION_REQUIRED` | Invalid or absent agent credential |
| 403 | `OPERATION_NOT_ALLOWED` | Credential or policy does not allow this operation |
| 404 | `NOTE_NOT_FOUND` / `SECTION_NOT_FOUND` | Target no longer resolves |
| 409 | `REVISION_CONFLICT` | Target changed after the agent read it |
| 409 | `IDEMPOTENCY_KEY_REUSED` | Same key used for different request content |
| 413 | `CONTENT_TOO_LARGE` | Direct-edit content exceeds limit |
| 422 | `REVIEW_REQUIRED` | Action is reviewable but falls outside direct-edit policy |
| 422 | `TARGET_NOT_EDITABLE` | Target is readable but is not eligible for this operation |
| 422 | `PROTECTED_PATH` | Server policy blocks the target path |
| 422 | `INVALID_MARKDOWN_OPERATION` | Operation would alter content outside its defined boundary |

Example policy rejection:

```json
{
  "code": "TARGET_NOT_EDITABLE",
  "message": "This note is readable but cannot receive direct agent appends.",
  "noteId": "note_...",
  "allowedOperations": [],
  "requiresReviewFor": ["replace_section"],
  "fallback": {
    "action": "create_note",
    "writableFolderIds": ["folder_inbox"]
  }
}
```

---

## 12. Audit, Backup, and Recovery

Each direct change must record:

```json
{
  "changeId": "change_...",
  "actor": "hermes-agent",
  "operation": "append_section",
  "path": "01 projects/Unlimit Payments.md",
  "noteId": "note_...",
  "sectionId": "section_...",
  "originConversationId": "chat_...",
  "requestExcerpt": "...",
  "beforeRevision": "sha256:...",
  "afterRevision": "sha256:...",
  "snapshotId": "snap_...",
  "unifiedDiff": "...",
  "appliedAt": "2026-08-21T09:17:04Z"
}
```

Backups can be implemented as per-change snapshots, Git commits, or both. The implementation must retain enough material to undo an allowed change during the configured undo window.

A daily audit digest should summarize:

- number of direct changes;
- affected paths;
- failed/conflicted changes;
- available undos; and
- any policy rejections.

---

## 13. Agent Interaction Contract

### 13.1 Normal path

The agent performs search/read/selection internally and asks no confirmation when the user’s target and change are clear and Tier 1 policy permits it.

After applying, it reports:

- what changed;
- path and section;
- a concise representation of the inserted content;
- change ID; and
- that Undo is available.

Example:

> Added the Poland rollout decision to `[[Unlimit Payments]]` → `## Notes`. Change `change_...`; say **undo** to revert it.

### 13.2 Ask only when needed

The agent asks for a choice when:

- two or more editable notes are similarly strong matches;
- the intended section is unclear;
- user language implies replacement/removal rather than appending;
- the mutation needs Tier 2 review;
- the request conflicts with policy; or
- revision conflict makes the original context stale.

### 13.3 No silent broad work

Even in direct-edit mode, the agent must not silently make several changes. A request involving more than one note remains Tier 2 unless the user explicitly asks for a batch action and approves its scope.

---

## 14. Migration From v0.1

The current proposal/review API remains deployed and backward compatible. This proposal adds a direct-edit lane; it does not change the current `obsidian-capture-api-spec.md`.

### Phase 1 — policy and observability

1. Implement `GET /v1/capabilities`.
2. Add `policy.directOperations` and allowed sections to search/note responses.
3. Add structured error bodies for authorization, policy, and revision failures.
4. Add snapshot/audit support reusable by both direct edits and v0.1 publication.

### Phase 2 — direct-create and direct-append

1. Implement `POST /v1/note-changes` with `dryRun` support.
2. Enable only `create_note` and `append_section` initially.
3. Enforce revision checks, path/heading policy, idempotency, atomic write, audit, and undo.
4. Test against a disposable synced-vault copy before enabling the live vault.

### Phase 3 — rollout to the agent

1. Run startup checks with `GET /v1/capabilities`.
2. Give Hermes only the new scoped agent token; remove the reviewer token from its environment.
3. Enable direct actions for a small allowed set of paths and headings.
4. Monitor the daily audit digest and expand policy only after normal use is stable.

### Phase 4 — retain review for high-risk work

Keep v0.1 change proposals for Tier 2 operations and present them only when direct policy deliberately declines the action.

---

## 15. Acceptance Criteria

The implementation is ready for live use only when these are verified against a disposable synced-vault copy and then the live vault:

- [ ] An agent can search and read an existing project note.
- [ ] Search indicates whether that note can be directly appended to and which sections are eligible.
- [ ] A valid `append_section` is applied atomically and returns a snapshot, diff, and change ID.
- [ ] A stale `baseRevision` produces `409 REVISION_CONFLICT` without changing the file.
- [ ] An attempt to write `.obsidian/`, `attachments/`, `templates/`, or `04 archive/` is rejected.
- [ ] An attempted whole-note overwrite, move, rename, or delete is rejected.
- [ ] Repeating the same idempotency key is safe.
- [ ] Reusing an idempotency key for a different operation is rejected.
- [ ] A user-requested undo succeeds only when no later edit conflicts.
- [ ] A user-requested undo fails safely with `409` after a later conflicting edit.
- [ ] The agent reports only successful writes as applied.
- [ ] The v0.1 proposal/review workflow remains available for Tier 2 operations.

---

## 16. Key Decision

The agent is a **controlled editor of the real vault**, not an inbox-only capture tool and not a generic filesystem client.

Routine, narrow, revision-checked edits apply directly and are recoverable. Ambiguous, broad, or destructive edits retain human review. This provides useful vault interaction without making every ordinary note update a multi-step approval ceremony.