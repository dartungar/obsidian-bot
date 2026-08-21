using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ObsidianBot.Configuration;

namespace ObsidianBot.Services;

public sealed class ProposalPublisher : BackgroundService
{
    private readonly ILogger<ProposalPublisher> _logger;
    private readonly ObsidianBotOptions _options;
    private readonly ProposalStore _store;
    private readonly VaultNotesService _notes;

    public ProposalPublisher(
        ILogger<ProposalPublisher> logger,
        ObsidianBotOptions options,
        ProposalStore store,
        VaultNotesService notes)
    {
        _logger = logger;
        _options = options;
        _store = store;
        _notes = notes;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var proposal = await _store.ClaimApprovedAsync(stoppingToken);
                if (proposal is null)
                {
                    await Task.Delay(
                        TimeSpan.FromSeconds(_options.PublisherPollIntervalSeconds),
                        stoppingToken);
                    continue;
                }

                await PublishAsync(proposal, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Proposal publisher loop failed; retrying");
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(_options.PublisherPollIntervalSeconds), stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
    }

    private async Task PublishAsync(StoredProposal proposal, CancellationToken ct)
    {
        try
        {
            if (proposal.ExpiresAt <= DateTimeOffset.UtcNow)
            {
                await CompleteAsync(proposal, "expired", null, null, null, null, "proposal_expired",
                    "The proposal expired before publication.", ct);
                return;
            }

            if (proposal.Type == "append_section")
            {
                await PublishAppendAsync(proposal, ct);
                return;
            }

            if (proposal.Type == "create_note")
            {
                await PublishCreateAsync(proposal, ct);
                return;
            }

            await CompleteAsync(proposal, "failed", null, null, null, null, "unsupported_type",
                "The proposal type is not supported by this publisher.", ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to publish proposal {ProposalId}", proposal.Id);
            await CompleteAsync(proposal, "failed", null, null, null, null, "publish_failed",
                "The publisher failed before completing the vault write.", ct);
        }
    }

    private async Task PublishAppendAsync(StoredProposal proposal, CancellationToken ct)
    {
        if (proposal.TargetNoteId is null || proposal.SectionId is null || proposal.BaseRevision is null)
        {
            await CompleteAsync(proposal, "failed", null, null, null, null, "invalid_proposal",
                "The append proposal is missing required target information.", ct);
            return;
        }

        var initial = await _notes.FindWritableNoteAsync(proposal.TargetNoteId, ct);
        if (initial is null)
        {
            await CompleteAsync(proposal, "failed", null, null, null, null, "target_unavailable",
                "The target note no longer exists or is not writable by current policy.", ct);
            return;
        }

        await using (await VaultWriteLock.AcquireAsync(initial.FullPath, ct))
        {
            var note = await _notes.FindWritableNoteAsync(proposal.TargetNoteId, ct);
            if (note is null)
            {
                await CompleteAsync(proposal, "failed", null, null, null, null, "target_unavailable",
                    "The target note no longer exists or is not writable by current policy.", ct);
                return;
            }

            if (!string.Equals(note.Revision, proposal.BaseRevision, StringComparison.Ordinal))
            {
                await CompleteAsync(proposal, "conflicted", null, note.Revision, null, note.Path, "revision_conflict",
                    "The target note changed after this proposal was created.", ct);
                return;
            }

            if (!_notes.TryAppendToSection(note, proposal.SectionId, proposal.ContentMarkdown, out var updated, out var error))
            {
                await CompleteAsync(proposal, "conflicted", null, note.Revision, null, note.Path, "section_conflict", error, ct);
                return;
            }

            var snapshotId = await WriteSnapshotAsync(proposal, note.Path, note.Content, ct);
            await WriteAtomicallyAsync(note.FullPath, updated, ct);
            await CompleteAsync(proposal, "applied", snapshotId, note.Revision, _notes.GetRevision(updated), note.Path, null, null, ct);
        }
    }

    private async Task PublishCreateAsync(StoredProposal proposal, CancellationToken ct)
    {
        if (!_notes.TryResolveWritableDestination(
                proposal.DestinationFolderId,
                proposal.DestinationPath is null ? null : Path.GetFileName(proposal.DestinationPath),
                out var relativePath,
                out var fullPath,
                out var error) ||
            !string.Equals(relativePath, proposal.DestinationPath, StringComparison.Ordinal))
        {
            await CompleteAsync(proposal, "failed", null, null, null, proposal.DestinationPath, "destination_unavailable",
                error.Length == 0 ? "The destination is no longer writable by current policy." : error, ct);
            return;
        }

        var frontmatter = string.IsNullOrWhiteSpace(proposal.FrontmatterJson)
            ? null
            : JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(proposal.FrontmatterJson);
        if (!_notes.TryBuildNewNoteContent(frontmatter, proposal.ContentMarkdown, out var content, out error))
        {
            await CompleteAsync(proposal, "failed", null, null, null, relativePath, "invalid_proposal", error, ct);
            return;
        }

        await using (await VaultWriteLock.AcquireAsync(fullPath, ct))
        {
            if (File.Exists(fullPath))
            {
                var currentContent = await File.ReadAllTextAsync(fullPath, ct);
                await CompleteAsync(proposal, "conflicted", null, _notes.GetRevision(currentContent), null, relativePath,
                    "destination_conflict", "A note now exists at the requested destination.", ct);
                return;
            }

            var snapshotId = await WriteSnapshotAsync(proposal, relativePath, null, ct);
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath) ?? _options.VaultPath);
            await WriteAtomicallyAsync(fullPath, content, ct);
            await CompleteAsync(proposal, "applied", snapshotId, null, _notes.GetRevision(content), relativePath, null, null, ct);
        }
    }

    private async Task CompleteAsync(
        StoredProposal proposal,
        string state,
        string? snapshotId,
        string? beforeRevision,
        string? afterRevision,
        string? finalPath,
        string? errorCode,
        string? errorMessage,
        CancellationToken ct)
    {
        await _store.CompletePublicationAsync(proposal.Id, new PublicationCompletion(
            state,
            state == "applied" ? DateTimeOffset.UtcNow : null,
            snapshotId,
            beforeRevision,
            afterRevision,
            finalPath,
            errorCode,
            errorMessage), ct);
    }

    private async Task<string> WriteSnapshotAsync(
        StoredProposal proposal,
        string targetPath,
        string? content,
        CancellationToken ct)
    {
        Directory.CreateDirectory(_options.ProposalSnapshotDirectory);
        var snapshotId = "snap_" + proposal.Id["proposal_".Length..];
        var snapshotPath = Path.Combine(_options.ProposalSnapshotDirectory, snapshotId + ".json");
        var json = JsonSerializer.Serialize(new
        {
            snapshot_id = snapshotId,
            proposal_id = proposal.Id,
            target_path = targetPath,
            existed = content is not null,
            content,
            captured_at = DateTimeOffset.UtcNow
        });
        var temporaryPath = snapshotPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        await File.WriteAllTextAsync(temporaryPath, json, new UTF8Encoding(false), ct);
        File.Move(temporaryPath, snapshotPath, overwrite: true);
        return snapshotId;
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
}
