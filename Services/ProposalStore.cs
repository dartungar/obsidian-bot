using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using ObsidianBot.Configuration;
using ObsidianBot.Models;

namespace ObsidianBot.Services;

public sealed class ProposalStore
{
    private static readonly TimeSpan PublicationClaimLease = TimeSpan.FromMinutes(5);

    private const string ProposalColumns = """
        id, type, state, created_at, expires_at, target_note_id, target_path, base_revision, section_id,
        destination_folder_id, destination_path, content_markdown, frontmatter_json, rationale,
        related_notes_json, conversation_id, request_excerpt, preview_diff, preview_markdown, preview_hash,
        warnings_json, review_decision, reviewed_at, review_comment, applied_at, snapshot_id,
        before_revision, after_revision, final_path, failure_code, failure_message, publication_claimed_at
        """;

    private readonly string _connectionString;
    private readonly SemaphoreSlim _lock = new(1, 1);

    public ProposalStore(ObsidianBotOptions options)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(options.ProposalDatabasePath) ?? ".");
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = options.ProposalDatabasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = true
        }.ToString();
        using var connection = OpenConnection();
        EnsureSchema(connection);
    }

    public async Task<ProposalCreateResult> CreateAsync(
        ProposalDraft draft,
        string idempotencyKey,
        string requestHash,
        CancellationToken ct)
    {
        await _lock.WaitAsync(ct);
        try
        {
            using var connection = OpenConnection();
            using var transaction = connection.BeginTransaction();
            var existing = GetIdempotency(connection, transaction, idempotencyKey);
            if (existing is not null)
            {
                if (!string.Equals(existing.Value.RequestHash, requestHash, StringComparison.Ordinal))
                {
                    throw new ProposalConflictException(
                        "Idempotency-Key was already used with a different proposal request.");
                }

                var replayProposal = GetProposal(connection, transaction, existing.Value.ProposalId)
                    ?? throw new InvalidOperationException("Idempotency record references a missing proposal.");
                transaction.Commit();
                return new ProposalCreateResult(replayProposal, true);
            }

            var proposal = StoredProposal.FromDraft(draft);
            InsertProposal(connection, transaction, proposal);
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = "INSERT INTO idempotency_keys(key, request_hash, proposal_id) VALUES (@key, @hash, @proposalId);";
                command.Parameters.AddWithValue("@key", idempotencyKey);
                command.Parameters.AddWithValue("@hash", requestHash);
                command.Parameters.AddWithValue("@proposalId", proposal.Id);
                command.ExecuteNonQuery();
            }

            InsertAuditEvent(connection, transaction, new AuditEventDraft(
                "agent",
                proposal.Id,
                proposal.ConversationId,
                proposal.TargetNoteId,
                proposal.TargetPath ?? proposal.DestinationPath,
                proposal.BaseRevision,
                null,
                proposal.PreviewDiff,
                null,
                "pending_review",
                null));
            transaction.Commit();
            return new ProposalCreateResult(proposal, false);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<StoredProposal?> GetAsync(string proposalId, CancellationToken ct)
    {
        await _lock.WaitAsync(ct);
        try
        {
            using var connection = OpenConnection();
            return GetProposal(connection, null, proposalId);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<IReadOnlyList<StoredProposal>> ListAsync(string? state, CancellationToken ct)
    {
        await _lock.WaitAsync(ct);
        try
        {
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = $"SELECT {ProposalColumns} FROM change_proposals" +
                                  (string.IsNullOrWhiteSpace(state) ? string.Empty : " WHERE state = @state") +
                                  " ORDER BY created_at DESC LIMIT 100;";
            if (!string.IsNullOrWhiteSpace(state))
            {
                command.Parameters.AddWithValue("@state", state.Trim());
            }

            using var reader = command.ExecuteReader();
            var proposals = new List<StoredProposal>();
            while (reader.Read())
            {
                proposals.Add(ReadProposal(reader));
            }

            return proposals;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<ReviewResult> ReviewAsync(
        string proposalId,
        ReviewProposalRequest request,
        CancellationToken ct)
    {
        await _lock.WaitAsync(ct);
        try
        {
            using var connection = OpenConnection();
            using var transaction = connection.BeginTransaction();
            var proposal = GetProposal(connection, transaction, proposalId);
            if (proposal is null)
            {
                return ReviewResult.NotFound;
            }

            if (proposal.State == "pending_review" && proposal.ExpiresAt <= DateTimeOffset.UtcNow)
            {
                proposal = proposal with { State = "expired" };
                UpdateState(connection, transaction, proposal);
                InsertAuditEvent(connection, transaction, new AuditEventDraft(
                    "publisher", proposal.Id, proposal.ConversationId, proposal.TargetNoteId,
                    proposal.TargetPath ?? proposal.DestinationPath, proposal.BaseRevision, null,
                    proposal.PreviewDiff, null, "expired", "proposal_expired"));
            }

            if (proposal.State != "pending_review")
            {
                transaction.Commit();
                return new ReviewResult(proposal, "Proposal is no longer pending review.");
            }

            var decision = request.Decision?.Trim().ToLowerInvariant();
            if (decision is not ("approved" or "rejected"))
            {
                transaction.Commit();
                return new ReviewResult(proposal, "decision must be either approved or rejected.");
            }

            if (decision == "approved" &&
                !string.Equals(request.ApprovedPreviewHash, proposal.PreviewHash, StringComparison.Ordinal))
            {
                transaction.Commit();
                return new ReviewResult(proposal, "approved_preview_hash does not match the authoritative preview.");
            }

            proposal = proposal with
            {
                State = decision == "approved" ? "approved" : "rejected",
                ReviewDecision = decision,
                ReviewedAt = DateTimeOffset.UtcNow,
                ReviewComment = string.IsNullOrWhiteSpace(request.Comment) ? null : request.Comment.Trim()
            };
            UpdateState(connection, transaction, proposal);
            InsertAuditEvent(connection, transaction, new AuditEventDraft(
                "human", proposal.Id, proposal.ConversationId, proposal.TargetNoteId,
                proposal.TargetPath ?? proposal.DestinationPath, proposal.BaseRevision, null,
                proposal.PreviewDiff, null, proposal.State, null));
            transaction.Commit();
            return new ReviewResult(proposal, null);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<StoredProposal?> ClaimApprovedAsync(CancellationToken ct)
    {
        await _lock.WaitAsync(ct);
        try
        {
            using var connection = OpenConnection();
            using var transaction = connection.BeginTransaction();
            var claimTime = DateTimeOffset.UtcNow;
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = $"""
                SELECT {ProposalColumns}
                FROM change_proposals
                WHERE state = 'approved'
                   OR (state = 'publishing' AND
                       (publication_claimed_at IS NULL OR publication_claimed_at <= @staleBefore))
                ORDER BY created_at
                LIMIT 1;
                """;
            command.Parameters.AddWithValue("@staleBefore", FormatTime(claimTime - PublicationClaimLease));
            using var reader = command.ExecuteReader();
            if (!reader.Read())
            {
                transaction.Commit();
                return null;
            }

            var proposal = ReadProposal(reader) with
            {
                State = "publishing",
                PublicationClaimedAt = claimTime
            };
            reader.Close();
            UpdateState(connection, transaction, proposal);
            transaction.Commit();
            return proposal;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task CompletePublicationAsync(
        string proposalId,
        PublicationCompletion completion,
        CancellationToken ct)
    {
        await _lock.WaitAsync(ct);
        try
        {
            using var connection = OpenConnection();
            using var transaction = connection.BeginTransaction();
            var proposal = GetProposal(connection, transaction, proposalId)
                ?? throw new InvalidOperationException("Cannot complete a missing proposal.");
            proposal = proposal with
            {
                State = completion.State,
                AppliedAt = completion.AppliedAt,
                SnapshotId = completion.SnapshotId,
                BeforeRevision = completion.BeforeRevision,
                AfterRevision = completion.AfterRevision,
                FinalPath = completion.FinalPath,
                FailureCode = completion.ErrorCode,
                FailureMessage = completion.ErrorMessage,
                PublicationClaimedAt = null
            };
            UpdateState(connection, transaction, proposal);
            InsertAuditEvent(connection, transaction, new AuditEventDraft(
                "publisher", proposal.Id, proposal.ConversationId, proposal.TargetNoteId,
                proposal.FinalPath ?? proposal.TargetPath ?? proposal.DestinationPath,
                proposal.BeforeRevision ?? proposal.BaseRevision, proposal.AfterRevision,
                proposal.PreviewDiff, proposal.SnapshotId, proposal.State, proposal.FailureCode));
            transaction.Commit();
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<IReadOnlyList<AuditEventResponse>> ListAuditEventsAsync(string? proposalId, CancellationToken ct)
    {
        await _lock.WaitAsync(ct);
        try
        {
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT id, occurred_at, actor_type, proposal_id, conversation_id, target_note_id, target_path,
                       old_revision, new_revision, final_diff, snapshot_id, status, error_code
                FROM audit_events
                """ + (string.IsNullOrWhiteSpace(proposalId) ? string.Empty : " WHERE proposal_id = @proposalId") +
                " ORDER BY occurred_at DESC LIMIT 200;";
            if (!string.IsNullOrWhiteSpace(proposalId))
            {
                command.Parameters.AddWithValue("@proposalId", proposalId);
            }

            using var reader = command.ExecuteReader();
            var events = new List<AuditEventResponse>();
            while (reader.Read())
            {
                events.Add(new AuditEventResponse(
                    reader.GetString(0),
                    ParseTime(reader.GetString(1)),
                    reader.GetString(2),
                    reader.GetString(3),
                    GetNullableString(reader, 4),
                    GetNullableString(reader, 5),
                    GetNullableString(reader, 6),
                    GetNullableString(reader, 7),
                    GetNullableString(reader, 8),
                    GetNullableString(reader, 9),
                    GetNullableString(reader, 10),
                    reader.GetString(11),
                    GetNullableString(reader, 12)));
            }

            return events;
        }
        finally
        {
            _lock.Release();
        }
    }

    private SqliteConnection OpenConnection()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        using var pragma = connection.CreateCommand();
        pragma.CommandText = "PRAGMA foreign_keys = ON; PRAGMA busy_timeout = 5000;";
        pragma.ExecuteNonQuery();
        return connection;
    }

    private static void EnsureSchema(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            PRAGMA journal_mode = WAL;

            CREATE TABLE IF NOT EXISTS change_proposals (
                id TEXT PRIMARY KEY,
                type TEXT NOT NULL,
                state TEXT NOT NULL,
                created_at TEXT NOT NULL,
                expires_at TEXT NOT NULL,
                target_note_id TEXT NULL,
                target_path TEXT NULL,
                base_revision TEXT NULL,
                section_id TEXT NULL,
                destination_folder_id TEXT NULL,
                destination_path TEXT NULL,
                content_markdown TEXT NOT NULL,
                frontmatter_json TEXT NULL,
                rationale TEXT NOT NULL,
                related_notes_json TEXT NOT NULL,
                conversation_id TEXT NULL,
                request_excerpt TEXT NULL,
                preview_diff TEXT NOT NULL,
                preview_markdown TEXT NOT NULL,
                preview_hash TEXT NOT NULL,
                warnings_json TEXT NOT NULL,
                review_decision TEXT NULL,
                reviewed_at TEXT NULL,
                review_comment TEXT NULL,
                applied_at TEXT NULL,
                snapshot_id TEXT NULL,
                before_revision TEXT NULL,
                after_revision TEXT NULL,
                final_path TEXT NULL,
                failure_code TEXT NULL,
                failure_message TEXT NULL,
                publication_claimed_at TEXT NULL
            );

            CREATE INDEX IF NOT EXISTS ix_change_proposals_state ON change_proposals(state, created_at);

            CREATE TABLE IF NOT EXISTS idempotency_keys (
                key TEXT PRIMARY KEY,
                request_hash TEXT NOT NULL,
                proposal_id TEXT NOT NULL REFERENCES change_proposals(id)
            );

            CREATE TABLE IF NOT EXISTS audit_events (
                id TEXT PRIMARY KEY,
                occurred_at TEXT NOT NULL,
                actor_type TEXT NOT NULL,
                proposal_id TEXT NOT NULL REFERENCES change_proposals(id),
                conversation_id TEXT NULL,
                target_note_id TEXT NULL,
                target_path TEXT NULL,
                old_revision TEXT NULL,
                new_revision TEXT NULL,
                final_diff TEXT NULL,
                snapshot_id TEXT NULL,
                status TEXT NOT NULL,
                error_code TEXT NULL
            );

            CREATE INDEX IF NOT EXISTS ix_audit_events_proposal ON audit_events(proposal_id, occurred_at DESC);
            """;
        command.ExecuteNonQuery();
        EnsureColumn(connection, "change_proposals", "publication_claimed_at", "TEXT NULL");
    }

    private static void EnsureColumn(
        SqliteConnection connection,
        string table,
        string column,
        string definition)
    {
        using var columns = connection.CreateCommand();
        columns.CommandText = $"PRAGMA table_info({table});";
        using var reader = columns.ExecuteReader();
        while (reader.Read())
        {
            if (string.Equals(reader.GetString(1), column, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }
        }

        reader.Close();
        using var alter = connection.CreateCommand();
        alter.CommandText = $"ALTER TABLE {table} ADD COLUMN {column} {definition};";
        alter.ExecuteNonQuery();
    }

    private static void InsertProposal(SqliteConnection connection, SqliteTransaction transaction, StoredProposal proposal)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO change_proposals (
                id, type, state, created_at, expires_at, target_note_id, target_path, base_revision, section_id,
                destination_folder_id, destination_path, content_markdown, frontmatter_json, rationale,
                related_notes_json, conversation_id, request_excerpt, preview_diff, preview_markdown, preview_hash,
                warnings_json, review_decision, reviewed_at, review_comment, applied_at, snapshot_id,
                before_revision, after_revision, final_path, failure_code, failure_message, publication_claimed_at)
            VALUES (
                @id, @type, @state, @createdAt, @expiresAt, @targetNoteId, @targetPath, @baseRevision, @sectionId,
                @destinationFolderId, @destinationPath, @contentMarkdown, @frontmatterJson, @rationale,
                @relatedNotesJson, @conversationId, @requestExcerpt, @previewDiff, @previewMarkdown, @previewHash,
                @warningsJson, @reviewDecision, @reviewedAt, @reviewComment, @appliedAt, @snapshotId,
                @beforeRevision, @afterRevision, @finalPath, @failureCode, @failureMessage, @publicationClaimedAt);
            """;
        AddProposalParameters(command, proposal);
        command.ExecuteNonQuery();
    }

    private static void UpdateState(SqliteConnection connection, SqliteTransaction transaction, StoredProposal proposal)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE change_proposals
            SET state = @state, review_decision = @reviewDecision, reviewed_at = @reviewedAt,
                review_comment = @reviewComment, applied_at = @appliedAt, snapshot_id = @snapshotId,
                before_revision = @beforeRevision, after_revision = @afterRevision, final_path = @finalPath,
                failure_code = @failureCode, failure_message = @failureMessage,
                publication_claimed_at = @publicationClaimedAt
            WHERE id = @id;
            """;
        AddProposalParameters(command, proposal);
        command.ExecuteNonQuery();
    }

    private static void AddProposalParameters(SqliteCommand command, StoredProposal proposal)
    {
        command.Parameters.AddWithValue("@id", proposal.Id);
        command.Parameters.AddWithValue("@type", proposal.Type);
        command.Parameters.AddWithValue("@state", proposal.State);
        command.Parameters.AddWithValue("@createdAt", FormatTime(proposal.CreatedAt));
        command.Parameters.AddWithValue("@expiresAt", FormatTime(proposal.ExpiresAt));
        AddNullable(command, "@targetNoteId", proposal.TargetNoteId);
        AddNullable(command, "@targetPath", proposal.TargetPath);
        AddNullable(command, "@baseRevision", proposal.BaseRevision);
        AddNullable(command, "@sectionId", proposal.SectionId);
        AddNullable(command, "@destinationFolderId", proposal.DestinationFolderId);
        AddNullable(command, "@destinationPath", proposal.DestinationPath);
        command.Parameters.AddWithValue("@contentMarkdown", proposal.ContentMarkdown);
        AddNullable(command, "@frontmatterJson", proposal.FrontmatterJson);
        command.Parameters.AddWithValue("@rationale", proposal.Rationale);
        command.Parameters.AddWithValue("@relatedNotesJson", proposal.RelatedNotesJson);
        AddNullable(command, "@conversationId", proposal.ConversationId);
        AddNullable(command, "@requestExcerpt", proposal.RequestExcerpt);
        command.Parameters.AddWithValue("@previewDiff", proposal.PreviewDiff);
        command.Parameters.AddWithValue("@previewMarkdown", proposal.PreviewMarkdown);
        command.Parameters.AddWithValue("@previewHash", proposal.PreviewHash);
        command.Parameters.AddWithValue("@warningsJson", proposal.WarningsJson);
        AddNullable(command, "@reviewDecision", proposal.ReviewDecision);
        AddNullable(command, "@reviewedAt", proposal.ReviewedAt is null ? null : FormatTime(proposal.ReviewedAt.Value));
        AddNullable(command, "@reviewComment", proposal.ReviewComment);
        AddNullable(command, "@appliedAt", proposal.AppliedAt is null ? null : FormatTime(proposal.AppliedAt.Value));
        AddNullable(command, "@snapshotId", proposal.SnapshotId);
        AddNullable(command, "@beforeRevision", proposal.BeforeRevision);
        AddNullable(command, "@afterRevision", proposal.AfterRevision);
        AddNullable(command, "@finalPath", proposal.FinalPath);
        AddNullable(command, "@failureCode", proposal.FailureCode);
        AddNullable(command, "@failureMessage", proposal.FailureMessage);
        AddNullable(command, "@publicationClaimedAt", proposal.PublicationClaimedAt is null
            ? null
            : FormatTime(proposal.PublicationClaimedAt.Value));
    }

    private static StoredProposal? GetProposal(SqliteConnection connection, SqliteTransaction? transaction, string proposalId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"SELECT {ProposalColumns} FROM change_proposals WHERE id = @id;";
        command.Parameters.AddWithValue("@id", proposalId);
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadProposal(reader) : null;
    }

    private static (string RequestHash, string ProposalId)? GetIdempotency(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string key)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT request_hash, proposal_id FROM idempotency_keys WHERE key = @key;";
        command.Parameters.AddWithValue("@key", key);
        using var reader = command.ExecuteReader();
        return reader.Read() ? (reader.GetString(0), reader.GetString(1)) : null;
    }

    private static void InsertAuditEvent(SqliteConnection connection, SqliteTransaction transaction, AuditEventDraft draft)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO audit_events (
                id, occurred_at, actor_type, proposal_id, conversation_id, target_note_id, target_path,
                old_revision, new_revision, final_diff, snapshot_id, status, error_code)
            VALUES (
                @id, @occurredAt, @actorType, @proposalId, @conversationId, @targetNoteId, @targetPath,
                @oldRevision, @newRevision, @finalDiff, @snapshotId, @status, @errorCode);
            """;
        command.Parameters.AddWithValue("@id", "audit_" + RandomId());
        command.Parameters.AddWithValue("@occurredAt", FormatTime(DateTimeOffset.UtcNow));
        command.Parameters.AddWithValue("@actorType", draft.ActorType);
        command.Parameters.AddWithValue("@proposalId", draft.ProposalId);
        AddNullable(command, "@conversationId", draft.ConversationId);
        AddNullable(command, "@targetNoteId", draft.TargetNoteId);
        AddNullable(command, "@targetPath", draft.TargetPath);
        AddNullable(command, "@oldRevision", draft.OldRevision);
        AddNullable(command, "@newRevision", draft.NewRevision);
        AddNullable(command, "@finalDiff", draft.FinalDiff);
        AddNullable(command, "@snapshotId", draft.SnapshotId);
        command.Parameters.AddWithValue("@status", draft.Status);
        AddNullable(command, "@errorCode", draft.ErrorCode);
        command.ExecuteNonQuery();
    }

    private static StoredProposal ReadProposal(SqliteDataReader reader) => new(
        reader.GetString(0), reader.GetString(1), reader.GetString(2), ParseTime(reader.GetString(3)),
        ParseTime(reader.GetString(4)), GetNullableString(reader, 5), GetNullableString(reader, 6),
        GetNullableString(reader, 7), GetNullableString(reader, 8), GetNullableString(reader, 9),
        GetNullableString(reader, 10), reader.GetString(11), GetNullableString(reader, 12), reader.GetString(13),
        reader.GetString(14), GetNullableString(reader, 15), GetNullableString(reader, 16), reader.GetString(17),
        reader.GetString(18), reader.GetString(19), reader.GetString(20), GetNullableString(reader, 21),
        GetNullableTime(reader, 22), GetNullableString(reader, 23), GetNullableTime(reader, 24),
        GetNullableString(reader, 25), GetNullableString(reader, 26), GetNullableString(reader, 27),
        GetNullableString(reader, 28), GetNullableString(reader, 29), GetNullableString(reader, 30),
        GetNullableTime(reader, 31));

    private static void AddNullable(SqliteCommand command, string name, string? value) =>
        command.Parameters.AddWithValue(name, value ?? (object)DBNull.Value);

    private static string? GetNullableString(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

    private static DateTimeOffset? GetNullableTime(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : ParseTime(reader.GetString(ordinal));

    private static string FormatTime(DateTimeOffset value) => value.UtcDateTime.ToString("O", CultureInfo.InvariantCulture);

    private static DateTimeOffset ParseTime(string value) =>
        DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    private static string RandomId() => Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
}

public sealed record ProposalDraft(
    string Type,
    DateTimeOffset CreatedAt,
    DateTimeOffset ExpiresAt,
    string? TargetNoteId,
    string? TargetPath,
    string? BaseRevision,
    string? SectionId,
    string? DestinationFolderId,
    string? DestinationPath,
    string ContentMarkdown,
    string? FrontmatterJson,
    string Rationale,
    string RelatedNotesJson,
    string? ConversationId,
    string? RequestExcerpt,
    string PreviewDiff,
    string PreviewMarkdown,
    string PreviewHash,
    string WarningsJson);

public sealed record StoredProposal(
    string Id,
    string Type,
    string State,
    DateTimeOffset CreatedAt,
    DateTimeOffset ExpiresAt,
    string? TargetNoteId,
    string? TargetPath,
    string? BaseRevision,
    string? SectionId,
    string? DestinationFolderId,
    string? DestinationPath,
    string ContentMarkdown,
    string? FrontmatterJson,
    string Rationale,
    string RelatedNotesJson,
    string? ConversationId,
    string? RequestExcerpt,
    string PreviewDiff,
    string PreviewMarkdown,
    string PreviewHash,
    string WarningsJson,
    string? ReviewDecision,
    DateTimeOffset? ReviewedAt,
    string? ReviewComment,
    DateTimeOffset? AppliedAt,
    string? SnapshotId,
    string? BeforeRevision,
    string? AfterRevision,
    string? FinalPath,
    string? FailureCode,
    string? FailureMessage,
    DateTimeOffset? PublicationClaimedAt)
{
    public static StoredProposal FromDraft(ProposalDraft draft) => new(
        "proposal_" + Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant(),
        draft.Type, "pending_review", draft.CreatedAt, draft.ExpiresAt, draft.TargetNoteId, draft.TargetPath,
        draft.BaseRevision, draft.SectionId, draft.DestinationFolderId, draft.DestinationPath, draft.ContentMarkdown,
        draft.FrontmatterJson, draft.Rationale, draft.RelatedNotesJson, draft.ConversationId, draft.RequestExcerpt,
        draft.PreviewDiff, draft.PreviewMarkdown, draft.PreviewHash, draft.WarningsJson, null, null, null,
        null, null, null, null, null, null, null, null);
}

public sealed record ProposalCreateResult(StoredProposal Proposal, bool IsIdempotentReplay);

public sealed record ReviewResult(StoredProposal? Proposal, string? Error)
{
    public static ReviewResult NotFound { get; } = new(null, null);
}

public sealed record PublicationCompletion(
    string State,
    DateTimeOffset? AppliedAt,
    string? SnapshotId,
    string? BeforeRevision,
    string? AfterRevision,
    string? FinalPath,
    string? ErrorCode,
    string? ErrorMessage);

public sealed record AuditEventDraft(
    string ActorType,
    string ProposalId,
    string? ConversationId,
    string? TargetNoteId,
    string? TargetPath,
    string? OldRevision,
    string? NewRevision,
    string? FinalDiff,
    string? SnapshotId,
    string Status,
    string? ErrorCode);

public sealed class ProposalConflictException : Exception
{
    public ProposalConflictException(string message) : base(message)
    {
    }
}
