namespace ObsidianBot.Models;

public sealed class PendingCapture
{
    public string? TextContent { get; init; }
    public byte[]? MediaBytes { get; init; }
    public string MediaFileExtension { get; init; } = ".jpg";
    public bool IsTask { get; init; }

    public bool HasContent =>
        !string.IsNullOrWhiteSpace(TextContent) ||
        MediaBytes is { Length: > 0 };
}