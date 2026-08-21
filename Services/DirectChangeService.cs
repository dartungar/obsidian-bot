using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http;
using ObsidianBot.Configuration;
using ObsidianBot.Models;

namespace ObsidianBot.Services;

public sealed class DirectChangeService
{
    private static readonly Regex HeadingLinePattern = new(
        "^\\s{0,3}#{1,6}\\s+",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.Multiline);
    private static readonly Regex FrontmatterDelimiterPattern = new(
        "^---\\s*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.Multiline);
    private static readonly Regex TaskPattern = new(
        "^[-*] \\[ \\] \\S[^\\r\\n]*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly ObsidianBotOptions _options;
    private readonly VaultAccessPolicy _accessPolicy;
    private readonly VaultNotesService _notes;
    private readonly DirectChangeStore _store;
    private readonly DirectChangeSnapshotStore _snapshots;

    public DirectChangeService(
        ObsidianBotOptions options,
        VaultAccessPolicy accessPolicy,
        VaultNotesService notes,
        DirectChangeStore store,
        DirectChangeSnapshotStore snapshots)
    {
        _options = options;
        _accessPolicy = accessPolicy;
        _notes = notes;
        _store = store;
        _snapshots = snapshots;
    }

    public AgentCapabilitiesResponse GetCapabilities() => new(
        "v0.2",
        ["create_note", "append_section", "append_task"],
        _options.AgentDirectAllowedHeadings
            .Select(heading => (IReadOnlyList<string>)new[] { heading })
            .ToArray(),
        _notes.GetWritableFolders(),
        _accessPolicy.GetProtectedPathPrefixes(),
        _options.DirectChangeMaxContentBytes,
        (int)_options.DirectChangeUndoWindow.TotalSeconds);

    public async Task<DirectChangeExecutionResult> ApplyAsync(
        DirectNoteChangeRequest request,
        string actor,
        string idempotencyKey,
        CancellationToken ct)
    {
        ValidateIdempotencyKey(idempotencyKey);
        var operation = request.Operation?.Trim().ToLowerInvariant();
        if (operation is not ("create_note" or "append_section" or "append_task"))
        {
            throw InvalidRequest("operation must be one of: create_note, append_section, append_task.");
        }

        var rationale = request.Rationale?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(rationale))
        {
            throw InvalidRequest("rationale is required.");
        }

        if (rationale.Length > 4_000)
        {
            throw InvalidRequest("rationale exceeds the 4000 character policy limit.");
        }

        if (request.Origin is null ||
            string.IsNullOrWhiteSpace(request.Origin.ConversationId) ||
            string.IsNullOrWhiteSpace(request.Origin.RequestExcerpt))
        {
            throw InvalidRequest("origin.conversationId and origin.requestExcerpt are required.");
        }

        if (request.Origin.RequestExcerpt.Trim().Length > 8_000)
        {
            throw InvalidRequest("origin.requestExcerpt exceeds the 8000 character policy limit.");
        }

        var requestHash = Hash(JsonSerializer.Serialize(new
        {
            operation,
            request.NoteId,
            request.SectionId,
            request.BaseRevision,
            request.ContentMarkdown,
            request.TaskMarkdown,
            request.FolderId,
            request.Filename,
            request.OnConflict,
            request.Frontmatter,
            rationale,
            request.Origin,
            request.DryRun
        }));

        if (!request.DryRun)
        {
            var replay = await _store.GetByIdempotencyAsync(actor, idempotencyKey.Trim(), requestHash, ct);
            if (replay is not null)
            {
                return Replay(replay);
            }
        }

        return operation == "create_note"
            ? await ApplyCreateAsync(request, actor, idempotencyKey.Trim(), requestHash, rationale, ct)
            : await ApplyAppendAsync(request, operation, actor, idempotencyKey.Trim(), requestHash, rationale, ct);
    }

    public async Task<UndoDirectChangeResponse> UndoAsync(string changeId, string actor, CancellationToken ct)
    {
        var original = await _store.GetAsync(changeId, ct);
        if (original is null || !string.Equals(original.Actor, actor, StringComparison.Ordinal))
        {
            throw new DirectChangeException(
                StatusCodes.Status404NotFound,
                "CHANGE_NOT_FOUND",
                "The direct change was not found.");
        }

        if (original.Operation == "undo" || original.State != "applied" || original.SnapshotId is null)
        {
            throw new DirectChangeException(
                StatusCodes.Status422UnprocessableEntity,
                "UNDO_NOT_AVAILABLE",
                "This change is not eligible for undo.");
        }

        if (original.UndoneByChangeId is not null ||
            original.UndoExpiresAt is null ||
            original.UndoExpiresAt <= DateTimeOffset.UtcNow)
        {
            throw new DirectChangeException(
                StatusCodes.Status422UnprocessableEntity,
                "UNDO_NOT_AVAILABLE",
                "The undo window has expired or this change was already undone.");
        }

        if (original.TargetNoteId is null || original.AfterRevision is null)
        {
            throw new DirectChangeException(
                StatusCodes.Status422UnprocessableEntity,
                "UNDO_NOT_AVAILABLE",
                "The change does not have enough revision information to undo safely.");
        }

        var initial = await _notes.FindReadableNoteAsync(original.TargetNoteId, ct);
        if (initial is null || !_accessPolicy.CanWrite(original.TargetPath))
        {
            throw new DirectChangeException(
                StatusCodes.Status422UnprocessableEntity,
                "TARGET_NOT_EDITABLE",
                "The original target is no longer writable by current policy.",
                noteId: original.TargetNoteId);
        }

        var snapshot = await _snapshots.ReadAsync(original.SnapshotId, ct);
        if (snapshot is null || !string.Equals(snapshot.ChangeId, original.Id, StringComparison.Ordinal))
        {
            throw new DirectChangeException(
                StatusCodes.Status500InternalServerError,
                "SNAPSHOT_UNAVAILABLE",
                "The rollback snapshot is unavailable; no undo was applied.");
        }

        var undoDiff = BuildUndoDiff(original.TargetPath, original.Id);
        var undoDraft = new DirectChangeDraft(
            actor,
            "undo",
            original.TargetNoteId,
            original.TargetPath,
            original.SectionId,
            original.SectionHeadingPathJson,
            null,
            $"Undo {original.Id}.",
            original.ConversationId,
            original.RequestExcerpt,
            undoDiff,
            original.Id);
        var undo = await _store.CreateUndoAsync(undoDraft, ct);

        try
        {
            await using (await VaultWriteLock.AcquireAsync(initial.FullPath, ct))
            {
                var current = await _notes.FindReadableNoteAsync(original.TargetNoteId, ct);
                if (current is null || !string.Equals(current.Path, original.TargetPath, StringComparison.Ordinal))
                {
                    await FailAsync(undo, "NOTE_NOT_FOUND", "The target note no longer resolves.", null, ct);
                    throw new DirectChangeException(
                        StatusCodes.Status404NotFound,
                        "NOTE_NOT_FOUND",
                        "The target note no longer resolves.",
                        noteId: original.TargetNoteId);
                }

                if (!string.Equals(current.Revision, original.AfterRevision, StringComparison.Ordinal))
                {
                    await FailAsync(undo, "UNDO_CONFLICT", "The note changed after the direct change was applied.", current.Revision, ct);
                    throw new DirectChangeException(
                        StatusCodes.Status409Conflict,
                        "UNDO_CONFLICT",
                        "The note changed after the direct change was applied; no undo was applied.",
                        noteId: original.TargetNoteId,
                        expectedRevision: original.AfterRevision,
                        currentRevision: current.Revision,
                        recommendedAction: "review_required");
                }

                var undoSnapshotId = await _snapshots.WriteAsync(undo.Id, current.Path, current.Content, ct);
                string? afterRevision;
                if (!snapshot.Existed)
                {
                    File.Delete(current.FullPath);
                    afterRevision = null;
                }
                else
                {
                    await WriteAtomicallyAsync(current.FullPath, snapshot.Content ?? string.Empty, ct);
                    afterRevision = _notes.GetRevision(snapshot.Content ?? string.Empty);
                }

                await _store.CompleteAsync(undo.Id, new DirectChangeCompletion(
                    "applied",
                    undoSnapshotId,
                    current.Revision,
                    afterRevision,
                    null,
                    null,
                    null), ct);
                await _store.MarkUndoneAsync(original.Id, undo.Id, ct);

                return new UndoDirectChangeResponse(
                    undo.Id,
                    "applied",
                    original.Id,
                    current.Path,
                    undoSnapshotId,
                    current.Revision,
                    afterRevision);
            }
        }
        catch (DirectChangeException)
        {
            throw;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await FailIfPendingAsync(undo, "UNDO_FAILED", "The service failed before completing the undo.", null, ct);
            throw new DirectChangeException(
                StatusCodes.Status500InternalServerError,
                "UNDO_FAILED",
                "The service failed before completing the undo.");
        }
    }

    public async Task<DirectChangeResponse?> GetAsync(string changeId, string actor, CancellationToken ct)
    {
        var change = await _store.GetAsync(changeId, ct);
        return change is null || !string.Equals(change.Actor, actor, StringComparison.Ordinal)
            ? null
            : ToResponse(change);
    }

    private async Task<DirectChangeExecutionResult> ApplyAppendAsync(
        DirectNoteChangeRequest request,
        string operation,
        string actor,
        string idempotencyKey,
        string requestHash,
        string rationale,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.NoteId) ||
            string.IsNullOrWhiteSpace(request.SectionId) ||
            string.IsNullOrWhiteSpace(request.BaseRevision))
        {
            throw InvalidRequest($"{operation} requires noteId, sectionId, and baseRevision.");
        }

        var markdown = operation == "append_task"
            ? ValidateTaskMarkdown(request.TaskMarkdown)
            : ValidateAppendMarkdown(request.ContentMarkdown);
        var initial = await FindAppendTargetAsync(
            request.NoteId.Trim(),
            request.SectionId.Trim(),
            request.BaseRevision.Trim(),
            operation == "append_task",
            ct);
        var diff = BuildAppendDiff(initial.Note.Path, initial.Section.HeadingPath, markdown);
        var draft = new DirectChangeDraft(
            actor,
            operation,
            initial.Note.Id,
            initial.Note.Path,
            initial.Section.Id,
            JsonSerializer.Serialize(initial.Section.HeadingPath),
            markdown,
            rationale,
            TrimOrNull(request.Origin?.ConversationId),
            TrimOrNull(request.Origin?.RequestExcerpt),
            diff);

        if (request.DryRun)
        {
            if (!_notes.TryAppendToSection(initial.Note, initial.Section.Id, markdown, out var updated, out var error))
            {
                throw InvalidMarkdown(error);
            }

            return new DirectChangeExecutionResult(new DirectChangeResponse(
                null,
                "validated",
                operation,
                initial.Note.Path,
                new DirectChangeSection(initial.Section.Id, initial.Section.HeadingPath),
                null,
                initial.Note.Revision,
                _notes.GetRevision(updated),
                diff,
                new DirectChangeUndo(false, null)), false);
        }

        var created = await _store.CreateOrGetAsync(draft, idempotencyKey, requestHash, ct);
        if (created.IsIdempotentReplay)
        {
            return Replay(created.Change);
        }

        try
        {
            await using (await VaultWriteLock.AcquireAsync(initial.Note.FullPath, ct))
            {
                var current = await FindAppendTargetAsync(
                    request.NoteId.Trim(),
                    request.SectionId.Trim(),
                    request.BaseRevision.Trim(),
                    operation == "append_task",
                    ct);
                if (!_notes.TryAppendToSection(current.Note, current.Section.Id, markdown, out var updated, out var error))
                {
                    await FailAsync(created.Change, "INVALID_MARKDOWN_OPERATION", error, current.Note.Revision, ct);
                    throw InvalidMarkdown(error);
                }

                var snapshotId = await _snapshots.WriteAsync(created.Change.Id, current.Note.Path, current.Note.Content, ct);
                await WriteAtomicallyAsync(current.Note.FullPath, updated, ct);
                var afterRevision = _notes.GetRevision(updated);
                var expiresAt = DateTimeOffset.UtcNow.Add(_options.DirectChangeUndoWindow);
                await _store.CompleteAsync(created.Change.Id, new DirectChangeCompletion(
                    "applied",
                    snapshotId,
                    current.Note.Revision,
                    afterRevision,
                    expiresAt,
                    null,
                    null), ct);

                return new DirectChangeExecutionResult(new DirectChangeResponse(
                    created.Change.Id,
                    "applied",
                    operation,
                    current.Note.Path,
                    new DirectChangeSection(current.Section.Id, current.Section.HeadingPath),
                    snapshotId,
                    current.Note.Revision,
                    afterRevision,
                    diff,
                    new DirectChangeUndo(true, expiresAt)), false);
            }
        }
        catch (DirectChangeException ex)
        {
            await FailIfPendingAsync(created.Change, ex.Error.Code, ex.Error.Message, ex.Error.CurrentRevision, ct);
            throw;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await FailIfPendingAsync(created.Change, "WRITE_FAILED", "The service failed before completing the direct change.", null, ct);
            throw new DirectChangeException(
                StatusCodes.Status500InternalServerError,
                "WRITE_FAILED",
                "The service failed before completing the direct change.");
        }
    }

    private async Task<DirectChangeExecutionResult> ApplyCreateAsync(
        DirectNoteChangeRequest request,
        string actor,
        string idempotencyKey,
        string requestHash,
        string rationale,
        CancellationToken ct)
    {
        if (!string.Equals(request.OnConflict?.Trim(), "reject", StringComparison.OrdinalIgnoreCase))
        {
            throw InvalidRequest("onConflict must be reject for create_note.");
        }

        if (!_notes.TryResolveWritableDestination(
                request.FolderId,
                request.Filename,
                out var relativePath,
                out var fullPath,
                out var destinationError))
        {
            throw new DirectChangeException(
                StatusCodes.Status422UnprocessableEntity,
                "TARGET_NOT_EDITABLE",
                destinationError.Length == 0
                    ? "The requested destination is not directly writable by policy."
                    : destinationError,
                writableFolderIds: _notes.GetWritableFolders().Select(folder => folder.Id).ToArray());
        }

        var destinationDirectory = Path.GetDirectoryName(fullPath) ?? _options.VaultPath;
        if (!Directory.Exists(destinationDirectory))
        {
            throw new DirectChangeException(
                StatusCodes.Status422UnprocessableEntity,
                "TARGET_NOT_EDITABLE",
                "The configured writable folder is not available in the vault.",
                writableFolderIds: _notes.GetWritableFolders().Select(folder => folder.Id).ToArray());
        }

        var markdown = request.ContentMarkdown?.Trim() ?? string.Empty;
        if (!_notes.TryBuildNewNoteContent(request.Frontmatter, markdown, out var content, out var contentError))
        {
            throw InvalidRequest(contentError);
        }

        ValidateContentSize(content);
        if (File.Exists(fullPath))
        {
            throw new DirectChangeException(
                StatusCodes.Status409Conflict,
                "DESTINATION_CONFLICT",
                "A note already exists at the requested destination.");
        }

        var diff = BuildCreateDiff(relativePath, content);
        var draft = new DirectChangeDraft(
            actor,
            "create_note",
            _notes.GetNoteId(relativePath),
            relativePath,
            null,
            null,
            content,
            rationale,
            TrimOrNull(request.Origin?.ConversationId),
            TrimOrNull(request.Origin?.RequestExcerpt),
            diff);

        if (request.DryRun)
        {
            return new DirectChangeExecutionResult(new DirectChangeResponse(
                null,
                "validated",
                "create_note",
                relativePath,
                null,
                null,
                null,
                _notes.GetRevision(content),
                diff,
                new DirectChangeUndo(false, null)), false);
        }

        var created = await _store.CreateOrGetAsync(draft, idempotencyKey, requestHash, ct);
        if (created.IsIdempotentReplay)
        {
            return Replay(created.Change);
        }

        try
        {
            await using (await VaultWriteLock.AcquireAsync(fullPath, ct))
            {
                if (File.Exists(fullPath))
                {
                    var current = await File.ReadAllTextAsync(fullPath, ct);
                    await FailAsync(created.Change, "DESTINATION_CONFLICT", "A note already exists at the requested destination.", _notes.GetRevision(current), ct);
                    throw new DirectChangeException(
                        StatusCodes.Status409Conflict,
                        "DESTINATION_CONFLICT",
                        "A note already exists at the requested destination.");
                }

                var snapshotId = await _snapshots.WriteAsync(created.Change.Id, relativePath, null, ct);
                await WriteAtomicallyAsync(fullPath, content, ct);
                var afterRevision = _notes.GetRevision(content);
                var expiresAt = DateTimeOffset.UtcNow.Add(_options.DirectChangeUndoWindow);
                await _store.CompleteAsync(created.Change.Id, new DirectChangeCompletion(
                    "applied",
                    snapshotId,
                    null,
                    afterRevision,
                    expiresAt,
                    null,
                    null), ct);

                return new DirectChangeExecutionResult(new DirectChangeResponse(
                    created.Change.Id,
                    "applied",
                    "create_note",
                    relativePath,
                    null,
                    snapshotId,
                    null,
                    afterRevision,
                    diff,
                    new DirectChangeUndo(true, expiresAt)), false);
            }
        }
        catch (DirectChangeException ex)
        {
            await FailIfPendingAsync(created.Change, ex.Error.Code, ex.Error.Message, ex.Error.CurrentRevision, ct);
            throw;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await FailIfPendingAsync(created.Change, "WRITE_FAILED", "The service failed before completing the direct change.", null, ct);
            throw new DirectChangeException(
                StatusCodes.Status500InternalServerError,
                "WRITE_FAILED",
                "The service failed before completing the direct change.");
        }
    }

    private async Task<(VaultNote Note, VaultSection Section)> FindAppendTargetAsync(
        string noteId,
        string sectionId,
        string baseRevision,
        bool isTask,
        CancellationToken ct)
    {
        var note = await _notes.FindReadableNoteAsync(noteId, ct);
        if (note is null)
        {
            throw new DirectChangeException(
                StatusCodes.Status404NotFound,
                "NOTE_NOT_FOUND",
                "The target note was not found.",
                noteId: noteId);
        }

        var section = note.Sections.SingleOrDefault(candidate =>
            string.Equals(candidate.Id, sectionId, StringComparison.Ordinal));
        if (section is null)
        {
            throw new DirectChangeException(
                StatusCodes.Status404NotFound,
                "SECTION_NOT_FOUND",
                "The target section was not found.",
                noteId: noteId,
                sectionId: sectionId);
        }

        if (!_notes.CanDirectAppend(note, section, isTask))
        {
            var policy = _notes.GetNotePolicy(note);
            throw new DirectChangeException(
                StatusCodes.Status422UnprocessableEntity,
                "TARGET_NOT_EDITABLE",
                "This note is readable but cannot receive that direct append.",
                noteId: noteId,
                sectionId: sectionId,
                allowedOperations: policy.DirectOperations);
        }

        if (!string.Equals(note.Revision, baseRevision, StringComparison.Ordinal))
        {
            throw new DirectChangeException(
                StatusCodes.Status409Conflict,
                "REVISION_CONFLICT",
                "The note changed after it was read; no write was applied.",
                noteId: noteId,
                expectedRevision: baseRevision,
                currentRevision: note.Revision,
                recommendedAction: "read_and_retry");
        }

        return (note, section);
    }

    private DirectChangeExecutionResult Replay(StoredDirectChange change)
    {
        if (change.State == "applied")
        {
            return new DirectChangeExecutionResult(ToResponse(change), true);
        }

        if (change.State == "pending")
        {
            throw new DirectChangeException(
                StatusCodes.Status409Conflict,
                "CHANGE_IN_PROGRESS",
                "A matching direct change is still in progress; retry with the same Idempotency-Key.");
        }

        var status = change.ErrorCode is "REVISION_CONFLICT" or "DESTINATION_CONFLICT" or "UNDO_CONFLICT"
            ? StatusCodes.Status409Conflict
            : StatusCodes.Status422UnprocessableEntity;
        throw new DirectChangeException(
            status,
            change.ErrorCode ?? "DIRECT_CHANGE_FAILED",
            change.ErrorMessage ?? "The earlier direct change did not complete.",
            noteId: change.TargetNoteId);
    }

    private DirectChangeResponse ToResponse(StoredDirectChange change)
    {
        IReadOnlyList<string>? headingPath = null;
        if (!string.IsNullOrWhiteSpace(change.SectionHeadingPathJson))
        {
            try
            {
                headingPath = JsonSerializer.Deserialize<string[]>(change.SectionHeadingPathJson);
            }
            catch (JsonException)
            {
                headingPath = null;
            }
        }

        var undoAvailable = change.State == "applied" &&
                            change.Operation != "undo" &&
                            change.UndoneByChangeId is null &&
                            change.UndoExpiresAt > DateTimeOffset.UtcNow;
        return new DirectChangeResponse(
            change.Id,
            change.State,
            change.Operation,
            change.TargetPath,
            change.SectionId is null || headingPath is null ? null : new DirectChangeSection(change.SectionId, headingPath),
            change.SnapshotId,
            change.BeforeRevision,
            change.AfterRevision,
            change.UnifiedDiff,
            new DirectChangeUndo(undoAvailable, undoAvailable ? change.UndoExpiresAt : null));
    }

    private static void ValidateIdempotencyKey(string key)
    {
        if (string.IsNullOrWhiteSpace(key) || key.Length > 200)
        {
            throw InvalidRequest("Idempotency-Key is required and must be at most 200 characters.");
        }
    }

    private string ValidateAppendMarkdown(string? value)
    {
        var markdown = value?.Replace("\r\n", "\n").Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(markdown))
        {
            throw InvalidRequest("contentMarkdown is required.");
        }

        ValidateContentSize(markdown);
        if (markdown.IndexOf('\0') >= 0 || HeadingLinePattern.IsMatch(markdown) || FrontmatterDelimiterPattern.IsMatch(markdown))
        {
            throw InvalidMarkdown("Direct appends may not introduce headings, frontmatter delimiters, or binary content.");
        }

        return markdown;
    }

    private string ValidateTaskMarkdown(string? value)
    {
        var markdown = ValidateAppendMarkdown(value);
        if (!TaskPattern.IsMatch(markdown))
        {
            throw InvalidRequest("taskMarkdown must contain exactly one unchecked Markdown task line.");
        }

        return markdown;
    }

    private void ValidateContentSize(string content)
    {
        if (Encoding.UTF8.GetByteCount(content) > _options.DirectChangeMaxContentBytes)
        {
            throw new DirectChangeException(
                StatusCodes.Status413PayloadTooLarge,
                "CONTENT_TOO_LARGE",
                $"Direct content exceeds the {_options.DirectChangeMaxContentBytes} byte policy limit.");
        }
    }

    private static async Task WriteAtomicallyAsync(string destinationPath, string content, CancellationToken ct)
    {
        var temporaryPath = Path.Combine(
            Path.GetDirectoryName(destinationPath)!,
            "." + Path.GetFileName(destinationPath) + "." + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            await File.WriteAllTextAsync(temporaryPath, content, new UTF8Encoding(false), ct);
            File.Move(temporaryPath, destinationPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private static string BuildAppendDiff(string path, IReadOnlyList<string> headingPath, string markdown) =>
        string.Join("\n", new[]
        {
            $"--- a/{path}",
            $"+++ b/{path}",
            $"@@ ## {string.Join(" / ", headingPath)} @@"
        }.Concat(markdown.Replace("\r\n", "\n").Trim().Split('\n').Select(line => "+ " + line)));

    private static string BuildCreateDiff(string path, string content) =>
        string.Join("\n", new[] { "--- /dev/null", $"+++ b/{path}", "@@ -0,0 +1 @@" }
            .Concat(content.Replace("\r\n", "\n").TrimEnd('\n').Split('\n').Select(line => "+ " + line)));

    private static string BuildUndoDiff(string path, string originalChangeId) =>
        $"--- a/{path}\n+++ b/{path}\n@@ direct undo @@\n- Revert {originalChangeId}";

    private async Task FailAsync(
        StoredDirectChange change,
        string errorCode,
        string errorMessage,
        string? currentRevision,
        CancellationToken ct) =>
        await _store.CompleteAsync(change.Id, new DirectChangeCompletion(
            "failed", change.SnapshotId, change.BeforeRevision, currentRevision, null, errorCode, errorMessage), ct);

    private async Task FailIfPendingAsync(
        StoredDirectChange change,
        string errorCode,
        string errorMessage,
        string? currentRevision,
        CancellationToken ct)
    {
        var stored = await _store.GetAsync(change.Id, ct);
        if (stored?.State == "pending")
        {
            await _store.CompleteAsync(change.Id, new DirectChangeCompletion(
                "failed", stored.SnapshotId, stored.BeforeRevision, currentRevision, null, errorCode, errorMessage), ct);
        }
    }

    private static string? TrimOrNull(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string Hash(string value) =>
        "sha256:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private static DirectChangeException InvalidRequest(string message) =>
        new(StatusCodes.Status400BadRequest, "INVALID_REQUEST", message);

    private static DirectChangeException InvalidMarkdown(string message) =>
        new(StatusCodes.Status422UnprocessableEntity, "INVALID_MARKDOWN_OPERATION", message);
}

public sealed record DirectChangeExecutionResult(DirectChangeResponse Response, bool IsIdempotentReplay);

public sealed class DirectChangeException : Exception
{
    public DirectChangeException(
        int statusCode,
        string code,
        string message,
        string? noteId = null,
        string? sectionId = null,
        string? expectedRevision = null,
        string? currentRevision = null,
        string? recommendedAction = null,
        IReadOnlyList<string>? allowedOperations = null,
        IReadOnlyList<string>? writableFolderIds = null)
        : base(message)
    {
        StatusCode = statusCode;
        Error = new ApiErrorResponse(
            code,
            message,
            noteId,
            sectionId,
            expectedRevision,
            currentRevision,
            recommendedAction,
            null,
            null,
            allowedOperations,
            writableFolderIds);
    }

    public int StatusCode { get; }

    public ApiErrorResponse Error { get; }
}
