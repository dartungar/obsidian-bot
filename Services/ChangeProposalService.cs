using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ObsidianBot.Configuration;
using ObsidianBot.Models;

namespace ObsidianBot.Services;

public sealed class ChangeProposalService
{
    private readonly ObsidianBotOptions _options;
    private readonly VaultNotesService _notes;
    private readonly ProposalStore _store;

    public ChangeProposalService(
        ObsidianBotOptions options,
        VaultNotesService notes,
        ProposalStore store)
    {
        _options = options;
        _notes = notes;
        _store = store;
    }

    public async Task<ProposalCreateResult> CreateAsync(
        CreateChangeProposalRequest request,
        string idempotencyKey,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(idempotencyKey) || idempotencyKey.Length > 200)
        {
            throw new VaultApiException("Idempotency-Key is required and must be at most 200 characters.");
        }

        var type = request.Type?.Trim().ToLowerInvariant();
        if (type is not ("append_section" or "create_note"))
        {
            throw new VaultApiException("type must be one of: append_section, create_note.");
        }

        var markdown = request.ContentMarkdown?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(markdown))
        {
            throw new VaultApiException("content_markdown is required.");
        }

        if (markdown.Length > _options.ProposalMaxMarkdownLength)
        {
            throw new VaultApiException(
                $"content_markdown exceeds the {_options.ProposalMaxMarkdownLength} character policy limit.");
        }

        var rationale = request.Rationale?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(rationale))
        {
            throw new VaultApiException("rationale is required.");
        }

        if (rationale.Length > 4_000)
        {
            throw new VaultApiException("rationale exceeds the 4000 character policy limit.");
        }

        var relatedNotesJson = JsonSerializer.Serialize(request.RelatedNotes ?? Array.Empty<string>());
        var now = DateTimeOffset.UtcNow;
        ProposalDraft draft;
        if (type == "append_section")
        {
            draft = await BuildAppendDraftAsync(request, markdown, rationale, relatedNotesJson, now, ct);
        }
        else
        {
            draft = BuildCreateDraft(request, markdown, rationale, relatedNotesJson, now);
        }

        var requestHash = Hash(JsonSerializer.Serialize(new
        {
            type,
            request.Target,
            request.Destination,
            markdown,
            request.Frontmatter,
            rationale,
            request.RelatedNotes,
            request.Origin
        }));
        return await _store.CreateAsync(draft, idempotencyKey.Trim(), requestHash, ct);
    }

    public async Task<ProposalResponse?> GetAsync(string proposalId, CancellationToken ct)
    {
        var proposal = await _store.GetAsync(proposalId, ct);
        return proposal is null ? null : ToResponse(proposal);
    }

    public async Task<ProposalListResponse> ListAsync(string? state, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(state) &&
            state.Trim() is not ("pending_review" or "approved" or "rejected" or "expired" or "publishing" or "applied" or "conflicted" or "failed"))
        {
            throw new VaultApiException("state is not a recognized proposal state.");
        }

        var proposals = await _store.ListAsync(state, ct);
        return new ProposalListResponse(proposals.Select(ToResponse).ToArray());
    }

    public async Task<ReviewResult> ReviewAsync(string proposalId, ReviewProposalRequest request, CancellationToken ct) =>
        await _store.ReviewAsync(proposalId, request, ct);

    public async Task<PublicationResponse?> GetPublicationAsync(string proposalId, CancellationToken ct)
    {
        var proposal = await _store.GetAsync(proposalId, ct);
        if (proposal is null)
        {
            return null;
        }

        return new PublicationResponse(
            proposal.Id,
            proposal.State,
            proposal.AppliedAt,
            proposal.SnapshotId,
            proposal.BeforeRevision,
            proposal.AfterRevision,
            proposal.FinalPath,
            proposal.State is "conflicted" or "failed" ? proposal.FailureMessage : null,
            proposal.State == "conflicted" ? "create_a_new_proposal" : null);
    }

    public async Task<AuditEventListResponse> ListAuditEventsAsync(string? proposalId, CancellationToken ct) =>
        new(await _store.ListAuditEventsAsync(proposalId, ct));

    public ProposalResponse ToResponse(StoredProposal proposal)
    {
        var warnings = DeserializeStringList(proposal.WarningsJson);
        var review = proposal.ReviewDecision is null || proposal.ReviewedAt is null
            ? null
            : new ProposalReview(proposal.ReviewDecision, proposal.ReviewedAt.Value, proposal.ReviewComment);
        return new ProposalResponse(
            proposal.Id,
            proposal.State,
            proposal.CreatedAt,
            proposal.ExpiresAt,
            new ProposalSummary(
                proposal.Type,
                proposal.TargetPath,
                proposal.DestinationPath,
                proposal.TargetNoteId,
                proposal.SectionId),
            new ProposalPreview(proposal.PreviewDiff, proposal.PreviewMarkdown, proposal.PreviewHash),
            new ProposalValidation(true, warnings),
            review);
    }

    private async Task<ProposalDraft> BuildAppendDraftAsync(
        CreateChangeProposalRequest request,
        string markdown,
        string rationale,
        string relatedNotesJson,
        DateTimeOffset now,
        CancellationToken ct)
    {
        if (request.Target is null ||
            string.IsNullOrWhiteSpace(request.Target.NoteId) ||
            string.IsNullOrWhiteSpace(request.Target.BaseRevision) ||
            string.IsNullOrWhiteSpace(request.Target.SectionId))
        {
            throw new VaultApiException(
                "append_section requires target.note_id, target.base_revision, and target.section_id.");
        }

        var note = await _notes.FindWritableNoteAsync(request.Target.NoteId, ct);
        if (note is null)
        {
            throw new VaultApiException("The target note does not exist or is not writable by policy.");
        }

        if (!string.Equals(note.Revision, request.Target.BaseRevision, StringComparison.Ordinal))
        {
            throw new ProposalConflictException(
                "The target note changed after it was read. Read it again and create a new proposal.");
        }

        if (!_notes.TryAppendToSection(note, request.Target.SectionId, markdown, out _, out var error))
        {
            throw new VaultApiException(error);
        }

        var section = note.Sections.Single(candidate =>
            string.Equals(candidate.Id, request.Target.SectionId, StringComparison.Ordinal));
        var diff = BuildAppendDiff(note.Path, section.HeadingPath, markdown);
        var previewMarkdown = $"## {string.Join(" / ", section.HeadingPath)}\n\n{markdown}";
        return CreateDraft(
            "append_section",
            now,
            note.Id,
            note.Path,
            note.Revision,
            section.Id,
            null,
            null,
            markdown,
            null,
            rationale,
            relatedNotesJson,
            request.Origin,
            diff,
            previewMarkdown);
    }

    private ProposalDraft BuildCreateDraft(
        CreateChangeProposalRequest request,
        string markdown,
        string rationale,
        string relatedNotesJson,
        DateTimeOffset now)
    {
        var error = string.Empty;
        if (request.Destination is null ||
            !_notes.TryResolveWritableDestination(
                request.Destination.FolderId,
                request.Destination.Filename,
                out var relativePath,
                out var fullPath,
                out error))
        {
            throw new VaultApiException(error.Length == 0
                ? "create_note requires destination.folder_id and destination.filename."
                : error);
        }

        if (!string.Equals(request.Destination.OnConflict?.Trim(), "reject", StringComparison.OrdinalIgnoreCase))
        {
            throw new VaultApiException("destination.on_conflict must be reject in v0.1.");
        }

        if (File.Exists(fullPath))
        {
            throw new ProposalConflictException("A note already exists at the requested destination.");
        }

        if (!_notes.TryBuildNewNoteContent(request.Frontmatter, markdown, out var newContent, out error))
        {
            throw new VaultApiException(error);
        }

        var diff = BuildCreateDiff(relativePath, newContent);
        return CreateDraft(
            "create_note",
            now,
            _notes.GetNoteId(relativePath),
            null,
            null,
            null,
            request.Destination.FolderId,
            relativePath,
            markdown,
            request.Frontmatter is null ? null : JsonSerializer.Serialize(request.Frontmatter),
            rationale,
            relatedNotesJson,
            request.Origin,
            diff,
            newContent);
    }

    private ProposalDraft CreateDraft(
        string type,
        DateTimeOffset createdAt,
        string? targetNoteId,
        string? targetPath,
        string? baseRevision,
        string? sectionId,
        string? folderId,
        string? destinationPath,
        string markdown,
        string? frontmatterJson,
        string rationale,
        string relatedNotesJson,
        ProposalOriginRequest? origin,
        string diff,
        string previewMarkdown)
    {
        var previewHash = Hash($"{type}\n{targetPath ?? destinationPath}\n{diff}\n{previewMarkdown}");
        return new ProposalDraft(
            type,
            createdAt,
            createdAt.Add(_options.ProposalTtl),
            targetNoteId,
            targetPath,
            baseRevision,
            sectionId,
            folderId,
            destinationPath,
            markdown,
            frontmatterJson,
            rationale,
            relatedNotesJson,
            origin?.ConversationId?.Trim(),
            origin?.RequestExcerpt?.Trim(),
            diff,
            previewMarkdown,
            previewHash,
            "[]");
    }

    private static string BuildAppendDiff(string path, IReadOnlyList<string> headingPath, string markdown)
    {
        var additions = markdown.Replace("\r\n", "\n").Trim().Split('\n')
            .Select(line => "+ " + line);
        return string.Join("\n", new[]
        {
            $"--- a/{path}",
            $"+++ b/{path}",
            $"@@ ## {string.Join(" / ", headingPath)} @@"
        }.Concat(additions));
    }

    private static string BuildCreateDiff(string path, string content) =>
        string.Join("\n", new[] { "--- /dev/null", $"+++ b/{path}", "@@ -0,0 +1 @@" }
            .Concat(content.Replace("\r\n", "\n").TrimEnd('\n').Split('\n').Select(line => "+ " + line)));

    private static IReadOnlyList<string> DeserializeStringList(string value)
    {
        try
        {
            return JsonSerializer.Deserialize<string[]>(value) ?? Array.Empty<string>();
        }
        catch (JsonException)
        {
            return Array.Empty<string>();
        }
    }

    private static string Hash(string value) =>
        "sha256:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}
