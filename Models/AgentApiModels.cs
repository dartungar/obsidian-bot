using System.Text.Json;

namespace ObsidianBot.Models;

public sealed record AgentApiDiscoveryResponse(
    string Version,
    string Openapi,
    IReadOnlyList<string> Resources);

public sealed record AgentSearchResponse(
    IReadOnlyList<AgentNoteSearchResult> Data,
    AgentSearchMeta Meta);

public sealed record AgentSearchMeta(string Query, string Mode, IReadOnlyList<WritableFolder> WritableFolders);

public sealed record WritableFolder(string Id, string Path);

public sealed record AgentNoteSearchResult(
    string Id,
    string Path,
    string Title,
    IReadOnlyList<string> Aliases,
    double Score,
    string? Snippet,
    IReadOnlyList<SectionSummary> MatchingSections,
    string Revision,
    IReadOnlyDictionary<string, object?>? Frontmatter = null,
    IReadOnlyList<SectionSummary>? Headings = null,
    NotePolicy? Policy = null);

public sealed record SectionSummary(string Id, IReadOnlyList<string> HeadingPath, int Level);

public sealed record AgentSectionListResponse(IReadOnlyList<SectionSummary> Data);

public sealed record AgentNoteResponse(
    string Id,
    string Path,
    string Title,
    string Revision,
    IReadOnlyDictionary<string, object?>? Frontmatter,
    IReadOnlyList<SectionSummary>? Headings,
    string? Content,
    NotePolicy Policy);

public sealed record AgentSectionResponse(
    string Id,
    IReadOnlyList<string> HeadingPath,
    string NoteRevision,
    string Content,
    string BeforeContext,
    string AfterContext);

public sealed record NoteLinkReference(string NoteId, string Path, string? Context);

public sealed record AgentLinksResponse(
    IReadOnlyList<NoteLinkReference> Backlinks,
    IReadOnlyList<NoteLinkReference> OutgoingLinks,
    IReadOnlyList<NoteLinkReference> RelatedNotes,
    IReadOnlyList<string> Tags,
    NotePolicy Policy);

public sealed record NotePolicy(
    bool Readable,
    IReadOnlyList<string> DirectOperations,
    IReadOnlyList<string> AllowedSectionIds,
    IReadOnlyList<string> RequiresReviewFor);

public sealed record AgentCapabilitiesResponse(
    string ApiVersion,
    IReadOnlyList<string> DirectOperations,
    IReadOnlyList<IReadOnlyList<string>> AllowedHeadingPaths,
    IReadOnlyList<WritableFolder> WritableFolders,
    IReadOnlyList<string> ProtectedPathPrefixes,
    int MaxDirectContentBytes,
    int UndoWindowSeconds);

public sealed record DirectNoteChangeRequest(
    string? Operation,
    string? NoteId,
    string? SectionId,
    string? BaseRevision,
    string? ContentMarkdown,
    string? TaskMarkdown,
    string? FolderId,
    string? Filename,
    string? OnConflict,
    Dictionary<string, JsonElement>? Frontmatter,
    string? Rationale,
    ProposalOriginRequest? Origin,
    bool DryRun = false);

public sealed record DirectChangeSection(string Id, IReadOnlyList<string> HeadingPath);

public sealed record DirectChangeUndo(bool Available, DateTimeOffset? ExpiresAt);

public sealed record DirectChangeResponse(
    string? ChangeId,
    string Status,
    string Operation,
    string Path,
    DirectChangeSection? Section,
    string? SnapshotId,
    string? BeforeRevision,
    string? AfterRevision,
    string UnifiedDiff,
    DirectChangeUndo Undo);

public sealed record UndoDirectChangeResponse(
    string ChangeId,
    string Status,
    string RevertedChangeId,
    string Path,
    string? SnapshotId,
    string? BeforeRevision,
    string? AfterRevision);

public sealed record ApiErrorResponse(
    string Code,
    string Message,
    string? NoteId = null,
    string? SectionId = null,
    string? ExpectedRevision = null,
    string? CurrentRevision = null,
    string? RecommendedAction = null,
    string? RequestedOperation = null,
    string? Reason = null,
    IReadOnlyList<string>? AllowedOperations = null,
    IReadOnlyList<string>? WritableFolderIds = null);

public sealed record CreateChangeProposalRequest(
    string? Type,
    ProposalTargetRequest? Target,
    ProposalDestinationRequest? Destination,
    string? ContentMarkdown,
    Dictionary<string, JsonElement>? Frontmatter,
    string? Rationale,
    IReadOnlyList<string>? RelatedNotes,
    ProposalOriginRequest? Origin);

public sealed record ProposalTargetRequest(string? NoteId, string? BaseRevision, string? SectionId);

public sealed record ProposalDestinationRequest(string? FolderId, string? Filename, string? OnConflict);

public sealed record ProposalOriginRequest(string? ConversationId, string? RequestExcerpt);

public sealed record ReviewProposalRequest(string? Decision, string? ApprovedPreviewHash, string? Comment);

public sealed record ProposalResponse(
    string Id,
    string State,
    DateTimeOffset CreatedAt,
    DateTimeOffset ExpiresAt,
    ProposalSummary Proposal,
    ProposalPreview Preview,
    ProposalValidation Validation,
    ProposalReview? Review = null);

public sealed record ProposalSummary(
    string Type,
    string? TargetPath,
    string? DestinationPath,
    string? NoteId,
    string? SectionId);

public sealed record ProposalPreview(string UnifiedDiff, string RenderedMarkdown, string Hash);

public sealed record ProposalValidation(bool Valid, IReadOnlyList<string> Warnings);

public sealed record ProposalReview(string Decision, DateTimeOffset At, string? Comment);

public sealed record ProposalListResponse(IReadOnlyList<ProposalResponse> Data);

public sealed record PublicationResponse(
    string ProposalId,
    string State,
    DateTimeOffset? AppliedAt,
    string? SnapshotId,
    string? BeforeRevision,
    string? AfterRevision,
    string? FinalPath,
    string? Reason = null,
    string? Resolution = null);

public sealed record AuditEventResponse(
    string Id,
    DateTimeOffset OccurredAt,
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

public sealed record AuditEventListResponse(IReadOnlyList<AuditEventResponse> Data);
