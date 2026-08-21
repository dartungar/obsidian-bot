using System.Text;
using System.Text.Json;
using ObsidianBot.Configuration;

namespace ObsidianBot.Services;

public sealed class DirectChangeSnapshotStore
{
    private readonly ObsidianBotOptions _options;

    public DirectChangeSnapshotStore(ObsidianBotOptions options)
    {
        _options = options;
    }

    public async Task<string> WriteAsync(
        string changeId,
        string targetPath,
        string? content,
        CancellationToken ct)
    {
        Directory.CreateDirectory(_options.ProposalSnapshotDirectory);
        var snapshotId = "snap_" + changeId["change_".Length..];
        var snapshotPath = GetSnapshotPath(snapshotId);
        var temporaryPath = snapshotPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        var snapshot = new DirectChangeSnapshot(
            snapshotId,
            changeId,
            targetPath,
            content is not null,
            content,
            DateTimeOffset.UtcNow);
        try
        {
            await File.WriteAllTextAsync(
                temporaryPath,
                JsonSerializer.Serialize(snapshot),
                new UTF8Encoding(false),
                ct);
            File.Move(temporaryPath, snapshotPath, overwrite: false);
            return snapshotId;
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    public async Task<DirectChangeSnapshot?> ReadAsync(string snapshotId, CancellationToken ct)
    {
        if (!IsValidSnapshotId(snapshotId))
        {
            return null;
        }

        var path = GetSnapshotPath(snapshotId);
        if (!File.Exists(path))
        {
            return null;
        }

        await using var stream = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<DirectChangeSnapshot>(stream, cancellationToken: ct);
    }

    private string GetSnapshotPath(string snapshotId) =>
        Path.Combine(_options.ProposalSnapshotDirectory, snapshotId + ".json");

    private static bool IsValidSnapshotId(string snapshotId) =>
        snapshotId.StartsWith("snap_", StringComparison.Ordinal) &&
        snapshotId.Length == "snap_".Length + 32 &&
        snapshotId["snap_".Length..].All(char.IsAsciiHexDigit);
}

public sealed record DirectChangeSnapshot(
    string SnapshotId,
    string ChangeId,
    string TargetPath,
    bool Existed,
    string? Content,
    DateTimeOffset CapturedAt);
