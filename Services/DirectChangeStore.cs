using System.Globalization;
using Microsoft.Data.Sqlite;
using ObsidianBot.Configuration;

namespace ObsidianBot.Services;

/// <summary>
/// Durable state for direct vault edits. It deliberately lives beside proposal
/// state so snapshots and audit records survive API restarts.
/// </summary>
public sealed class DirectChangeStore
{
    private const string ChangeColumns = """
        id, actor, operation, state, created_at, target_note_id, target_path, section_id,
        section_heading_path_json, content_markdown, rationale, conversation_id, request_excerpt,
        unified_diff, snapshot_id, before_revision, after_revision, undo_expires_at,
        error_code, error_message, undo_of_change_id, undone_by_change_id
        """;

    private readonly string _connectionString;
    private readonly SemaphoreSlim _lock = new(1, 1);

    public DirectChangeStore(ObsidianBotOptions options)
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

    public async Task<DirectChangeCreateResult> CreateOrGetAsync(
        DirectChangeDraft draft,
        string idempotencyKey,
        string requestHash,
        CancellationToken ct)
    {
        await _lock.WaitAsync(ct);
        try
        {
            using var connection = OpenConnection();
            using var transaction = connection.BeginTransaction();
            var existing = GetIdempotency(connection, transaction, draft.Actor, idempotencyKey);
            if (existing is not null)
            {
                if (!string.Equals(existing.Value.RequestHash, requestHash, StringComparison.Ordinal))
                {
                    throw new DirectChangeException(
                        StatusCodes.Status409Conflict,
                        "IDEMPOTENCY_KEY_REUSED",
                        "Idempotency-Key was already used with a different direct change request.");
                }

                var replay = GetChange(connection, transaction, existing.Value.ChangeId)
                    ?? throw new InvalidOperationException("Idempotency record references a missing direct change.");
                transaction.Commit();
                return new DirectChangeCreateResult(replay, true);
            }

            var change = StoredDirectChange.FromDraft(draft);
            InsertChange(connection, transaction, change);
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = """
                    INSERT INTO direct_change_idempotency(actor, key, request_hash, change_id)
                    VALUES (@actor, @key, @hash, @changeId);
                    """;
                command.Parameters.AddWithValue("@actor", change.Actor);
                command.Parameters.AddWithValue("@key", idempotencyKey);
                command.Parameters.AddWithValue("@hash", requestHash);
                command.Parameters.AddWithValue("@changeId", change.Id);
                command.ExecuteNonQuery();
            }

            InsertAuditEvent(connection, transaction, change, "pending", null, null, null, null);
            transaction.Commit();
            return new DirectChangeCreateResult(change, false);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<StoredDirectChange> CreateUndoAsync(DirectChangeDraft draft, CancellationToken ct)
    {
        await _lock.WaitAsync(ct);
        try
        {
            using var connection = OpenConnection();
            using var transaction = connection.BeginTransaction();
            var change = StoredDirectChange.FromDraft(draft);
            InsertChange(connection, transaction, change);
            InsertAuditEvent(connection, transaction, change, "pending", null, null, null, null);
            transaction.Commit();
            return change;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<StoredDirectChange?> GetAsync(string changeId, CancellationToken ct)
    {
        await _lock.WaitAsync(ct);
        try
        {
            using var connection = OpenConnection();
            return GetChange(connection, null, changeId);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<StoredDirectChange?> GetByIdempotencyAsync(
        string actor,
        string idempotencyKey,
        string requestHash,
        CancellationToken ct)
    {
        await _lock.WaitAsync(ct);
        try
        {
            using var connection = OpenConnection();
            using var transaction = connection.BeginTransaction();
            var existing = GetIdempotency(connection, transaction, actor, idempotencyKey);
            if (existing is null)
            {
                transaction.Commit();
                return null;
            }

            if (!string.Equals(existing.Value.RequestHash, requestHash, StringComparison.Ordinal))
            {
                throw new DirectChangeException(
                    StatusCodes.Status409Conflict,
                    "IDEMPOTENCY_KEY_REUSED",
                    "Idempotency-Key was already used with a different direct change request.");
            }

            var change = GetChange(connection, transaction, existing.Value.ChangeId)
                ?? throw new InvalidOperationException("Idempotency record references a missing direct change.");
            transaction.Commit();
            return change;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task CompleteAsync(
        string changeId,
        DirectChangeCompletion completion,
        CancellationToken ct)
    {
        await _lock.WaitAsync(ct);
        try
        {
            using var connection = OpenConnection();
            using var transaction = connection.BeginTransaction();
            var change = GetChange(connection, transaction, changeId)
                ?? throw new InvalidOperationException("Cannot complete a missing direct change.");
            var completed = change with
            {
                State = completion.State,
                SnapshotId = completion.SnapshotId,
                BeforeRevision = completion.BeforeRevision,
                AfterRevision = completion.AfterRevision,
                UndoExpiresAt = completion.UndoExpiresAt,
                ErrorCode = completion.ErrorCode,
                ErrorMessage = completion.ErrorMessage
            };
            UpdateChange(connection, transaction, completed);
            InsertAuditEvent(
                connection,
                transaction,
                completed,
                completion.State,
                completion.BeforeRevision,
                completion.AfterRevision,
                completion.SnapshotId,
                completion.ErrorCode);
            transaction.Commit();
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task MarkUndoneAsync(string originalChangeId, string undoChangeId, CancellationToken ct)
    {
        await _lock.WaitAsync(ct);
        try
        {
            using var connection = OpenConnection();
            using var transaction = connection.BeginTransaction();
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                UPDATE direct_changes
                SET undone_by_change_id = @undoChangeId
                WHERE id = @originalChangeId AND undone_by_change_id IS NULL;
                """;
            command.Parameters.AddWithValue("@undoChangeId", undoChangeId);
            command.Parameters.AddWithValue("@originalChangeId", originalChangeId);
            if (command.ExecuteNonQuery() != 1)
            {
                throw new InvalidOperationException("The direct change was already undone.");
            }

            transaction.Commit();
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

            CREATE TABLE IF NOT EXISTS direct_changes (
                id TEXT PRIMARY KEY,
                actor TEXT NOT NULL,
                operation TEXT NOT NULL,
                state TEXT NOT NULL,
                created_at TEXT NOT NULL,
                target_note_id TEXT NULL,
                target_path TEXT NOT NULL,
                section_id TEXT NULL,
                section_heading_path_json TEXT NULL,
                content_markdown TEXT NULL,
                rationale TEXT NOT NULL,
                conversation_id TEXT NULL,
                request_excerpt TEXT NULL,
                unified_diff TEXT NOT NULL,
                snapshot_id TEXT NULL,
                before_revision TEXT NULL,
                after_revision TEXT NULL,
                undo_expires_at TEXT NULL,
                error_code TEXT NULL,
                error_message TEXT NULL,
                undo_of_change_id TEXT NULL,
                undone_by_change_id TEXT NULL
            );

            CREATE INDEX IF NOT EXISTS ix_direct_changes_actor_created
                ON direct_changes(actor, created_at DESC);

            CREATE TABLE IF NOT EXISTS direct_change_idempotency (
                actor TEXT NOT NULL,
                key TEXT NOT NULL,
                request_hash TEXT NOT NULL,
                change_id TEXT NOT NULL REFERENCES direct_changes(id),
                PRIMARY KEY(actor, key)
            );

            CREATE TABLE IF NOT EXISTS direct_change_audit_events (
                id TEXT PRIMARY KEY,
                occurred_at TEXT NOT NULL,
                actor TEXT NOT NULL,
                change_id TEXT NOT NULL REFERENCES direct_changes(id),
                operation TEXT NOT NULL,
                target_path TEXT NOT NULL,
                conversation_id TEXT NULL,
                target_note_id TEXT NULL,
                old_revision TEXT NULL,
                new_revision TEXT NULL,
                final_diff TEXT NOT NULL,
                snapshot_id TEXT NULL,
                status TEXT NOT NULL,
                error_code TEXT NULL
            );

            CREATE INDEX IF NOT EXISTS ix_direct_change_audit_events_change
                ON direct_change_audit_events(change_id, occurred_at DESC);
            """;
        command.ExecuteNonQuery();
    }

    private static void InsertChange(SqliteConnection connection, SqliteTransaction transaction, StoredDirectChange change)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO direct_changes (
                id, actor, operation, state, created_at, target_note_id, target_path, section_id,
                section_heading_path_json, content_markdown, rationale, conversation_id, request_excerpt,
                unified_diff, snapshot_id, before_revision, after_revision, undo_expires_at,
                error_code, error_message, undo_of_change_id, undone_by_change_id)
            VALUES (
                @id, @actor, @operation, @state, @createdAt, @targetNoteId, @targetPath, @sectionId,
                @sectionHeadingPathJson, @contentMarkdown, @rationale, @conversationId, @requestExcerpt,
                @unifiedDiff, @snapshotId, @beforeRevision, @afterRevision, @undoExpiresAt,
                @errorCode, @errorMessage, @undoOfChangeId, @undoneByChangeId);
            """;
        AddParameters(command, change);
        command.ExecuteNonQuery();
    }

    private static void UpdateChange(SqliteConnection connection, SqliteTransaction transaction, StoredDirectChange change)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE direct_changes
            SET state = @state, snapshot_id = @snapshotId, before_revision = @beforeRevision,
                after_revision = @afterRevision, undo_expires_at = @undoExpiresAt,
                error_code = @errorCode, error_message = @errorMessage,
                undone_by_change_id = @undoneByChangeId
            WHERE id = @id;
            """;
        AddParameters(command, change);
        command.ExecuteNonQuery();
    }

    private static void AddParameters(SqliteCommand command, StoredDirectChange change)
    {
        command.Parameters.AddWithValue("@id", change.Id);
        command.Parameters.AddWithValue("@actor", change.Actor);
        command.Parameters.AddWithValue("@operation", change.Operation);
        command.Parameters.AddWithValue("@state", change.State);
        command.Parameters.AddWithValue("@createdAt", FormatTime(change.CreatedAt));
        AddNullable(command, "@targetNoteId", change.TargetNoteId);
        command.Parameters.AddWithValue("@targetPath", change.TargetPath);
        AddNullable(command, "@sectionId", change.SectionId);
        AddNullable(command, "@sectionHeadingPathJson", change.SectionHeadingPathJson);
        AddNullable(command, "@contentMarkdown", change.ContentMarkdown);
        command.Parameters.AddWithValue("@rationale", change.Rationale);
        AddNullable(command, "@conversationId", change.ConversationId);
        AddNullable(command, "@requestExcerpt", change.RequestExcerpt);
        command.Parameters.AddWithValue("@unifiedDiff", change.UnifiedDiff);
        AddNullable(command, "@snapshotId", change.SnapshotId);
        AddNullable(command, "@beforeRevision", change.BeforeRevision);
        AddNullable(command, "@afterRevision", change.AfterRevision);
        AddNullable(command, "@undoExpiresAt", change.UndoExpiresAt is null ? null : FormatTime(change.UndoExpiresAt.Value));
        AddNullable(command, "@errorCode", change.ErrorCode);
        AddNullable(command, "@errorMessage", change.ErrorMessage);
        AddNullable(command, "@undoOfChangeId", change.UndoOfChangeId);
        AddNullable(command, "@undoneByChangeId", change.UndoneByChangeId);
    }

    private static StoredDirectChange? GetChange(SqliteConnection connection, SqliteTransaction? transaction, string changeId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"SELECT {ChangeColumns} FROM direct_changes WHERE id = @id;";
        command.Parameters.AddWithValue("@id", changeId);
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadChange(reader) : null;
    }

    private static (string RequestHash, string ChangeId)? GetIdempotency(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string actor,
        string key)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT request_hash, change_id
            FROM direct_change_idempotency
            WHERE actor = @actor AND key = @key;
            """;
        command.Parameters.AddWithValue("@actor", actor);
        command.Parameters.AddWithValue("@key", key);
        using var reader = command.ExecuteReader();
        return reader.Read() ? (reader.GetString(0), reader.GetString(1)) : null;
    }

    private static void InsertAuditEvent(
        SqliteConnection connection,
        SqliteTransaction transaction,
        StoredDirectChange change,
        string status,
        string? oldRevision,
        string? newRevision,
        string? snapshotId,
        string? errorCode)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO direct_change_audit_events (
                id, occurred_at, actor, change_id, operation, target_path, conversation_id, target_note_id,
                old_revision, new_revision, final_diff, snapshot_id, status, error_code)
            VALUES (
                @id, @occurredAt, @actor, @changeId, @operation, @targetPath, @conversationId, @targetNoteId,
                @oldRevision, @newRevision, @finalDiff, @snapshotId, @status, @errorCode);
            """;
        command.Parameters.AddWithValue("@id", "audit_" + Guid.NewGuid().ToString("N"));
        command.Parameters.AddWithValue("@occurredAt", FormatTime(DateTimeOffset.UtcNow));
        command.Parameters.AddWithValue("@actor", change.Actor);
        command.Parameters.AddWithValue("@changeId", change.Id);
        command.Parameters.AddWithValue("@operation", change.Operation);
        command.Parameters.AddWithValue("@targetPath", change.TargetPath);
        AddNullable(command, "@conversationId", change.ConversationId);
        AddNullable(command, "@targetNoteId", change.TargetNoteId);
        AddNullable(command, "@oldRevision", oldRevision);
        AddNullable(command, "@newRevision", newRevision);
        command.Parameters.AddWithValue("@finalDiff", change.UnifiedDiff);
        AddNullable(command, "@snapshotId", snapshotId);
        command.Parameters.AddWithValue("@status", status);
        AddNullable(command, "@errorCode", errorCode);
        command.ExecuteNonQuery();
    }

    private static StoredDirectChange ReadChange(SqliteDataReader reader) => new(
        reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), ParseTime(reader.GetString(4)),
        GetNullableString(reader, 5), reader.GetString(6), GetNullableString(reader, 7), GetNullableString(reader, 8),
        GetNullableString(reader, 9), reader.GetString(10), GetNullableString(reader, 11), GetNullableString(reader, 12),
        reader.GetString(13), GetNullableString(reader, 14), GetNullableString(reader, 15), GetNullableString(reader, 16),
        GetNullableTime(reader, 17), GetNullableString(reader, 18), GetNullableString(reader, 19), GetNullableString(reader, 20),
        GetNullableString(reader, 21));

    private static void AddNullable(SqliteCommand command, string name, string? value) =>
        command.Parameters.AddWithValue(name, value ?? (object)DBNull.Value);

    private static string? GetNullableString(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

    private static DateTimeOffset? GetNullableTime(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : ParseTime(reader.GetString(ordinal));

    private static string FormatTime(DateTimeOffset value) => value.UtcDateTime.ToString("O", CultureInfo.InvariantCulture);

    private static DateTimeOffset ParseTime(string value) =>
        DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
}

public sealed record DirectChangeDraft(
    string Actor,
    string Operation,
    string? TargetNoteId,
    string TargetPath,
    string? SectionId,
    string? SectionHeadingPathJson,
    string? ContentMarkdown,
    string Rationale,
    string? ConversationId,
    string? RequestExcerpt,
    string UnifiedDiff,
    string? UndoOfChangeId = null);

public sealed record StoredDirectChange(
    string Id,
    string Actor,
    string Operation,
    string State,
    DateTimeOffset CreatedAt,
    string? TargetNoteId,
    string TargetPath,
    string? SectionId,
    string? SectionHeadingPathJson,
    string? ContentMarkdown,
    string Rationale,
    string? ConversationId,
    string? RequestExcerpt,
    string UnifiedDiff,
    string? SnapshotId,
    string? BeforeRevision,
    string? AfterRevision,
    DateTimeOffset? UndoExpiresAt,
    string? ErrorCode,
    string? ErrorMessage,
    string? UndoOfChangeId,
    string? UndoneByChangeId)
{
    public static StoredDirectChange FromDraft(DirectChangeDraft draft) => new(
        "change_" + Guid.NewGuid().ToString("N"),
        draft.Actor,
        draft.Operation,
        "pending",
        DateTimeOffset.UtcNow,
        draft.TargetNoteId,
        draft.TargetPath,
        draft.SectionId,
        draft.SectionHeadingPathJson,
        draft.ContentMarkdown,
        draft.Rationale,
        draft.ConversationId,
        draft.RequestExcerpt,
        draft.UnifiedDiff,
        null,
        null,
        null,
        null,
        null,
        null,
        draft.UndoOfChangeId,
        null);
}

public sealed record DirectChangeCreateResult(StoredDirectChange Change, bool IsIdempotentReplay);

public sealed record DirectChangeCompletion(
    string State,
    string? SnapshotId,
    string? BeforeRevision,
    string? AfterRevision,
    DateTimeOffset? UndoExpiresAt,
    string? ErrorCode,
    string? ErrorMessage);
