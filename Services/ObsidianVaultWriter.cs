using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using ObsidianBot.Configuration;
using ObsidianBot.Models;

namespace ObsidianBot.Services;

public sealed class ObsidianVaultWriter
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> FileLocks = new(StringComparer.Ordinal);

    private readonly ObsidianBotOptions _options;

    public ObsidianVaultWriter(ObsidianBotOptions options)
    {
        _options = options;
        Directory.CreateDirectory(_options.VaultPath);
    }

    public DateOnly GetLocalDateToday()
    {
        var now = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, _options.TimeZone);
        return DateOnly.FromDateTime(now.DateTime);
    }

    public Task<SaveResult> SaveToInboxAsync(PendingCapture pending, CancellationToken ct)
    {
        var notePath = ResolveVaultPath(_options.InboxNotePath);
        return SaveToNoteAsync(notePath, "inbox note", pending, prependDivider: true, bulletText: false, ct);
    }

    public Task<SaveResult> SaveToDailyNoteAsync(DateOnly date, PendingCapture pending, CancellationToken ct)
    {
        var dailyDirectory = Path.GetDirectoryName(_options.DailyNotesPattern) ?? string.Empty;
        var relativePath = Path.Combine(dailyDirectory, $"{date:yyyy-MM-dd}.md");
        var notePath = ResolveVaultPath(relativePath);
        return SaveToNoteAsync(notePath, $"daily note for {date:yyyy-MM-dd}", pending, prependDivider: false, bulletText: true, ct);
    }

    private async Task<SaveResult> SaveToNoteAsync(
        string notePath,
        string target,
        PendingCapture pending,
        bool prependDivider,
        bool bulletText,
        CancellationToken ct)
    {
        EnsureMarkdownPath(notePath);
        Directory.CreateDirectory(Path.GetDirectoryName(notePath) ?? _options.VaultPath);

        string? mediaRelativePath = null;
        if (pending.MediaBytes is { Length: > 0 })
        {
            mediaRelativePath = await SaveMediaAsync(pending.MediaBytes, pending.MediaFileExtension, ct);
        }

        var lines = new List<string>();
        if (!string.IsNullOrWhiteSpace(pending.TextContent))
        {
            var text = pending.TextContent.Trim();
            lines.Add(bulletText ? $"- {text}" : text);
        }

        if (!string.IsNullOrWhiteSpace(mediaRelativePath))
        {
            lines.Add($"![[{mediaRelativePath}|300]]");
        }

        var entry = string.Join("\n", lines).Trim();
        if (string.IsNullOrWhiteSpace(entry))
        {
            throw new InvalidOperationException("Empty capture payload.");
        }

        if (prependDivider)
        {
            entry = $"***\n{entry}";
        }

        var fileLock = FileLocks.GetOrAdd(notePath, static _ => new SemaphoreSlim(1, 1));
        await fileLock.WaitAsync(ct);
        try
        {
            await AppendToFileAsync(notePath, entry, ct);
        }
        finally
        {
            fileLock.Release();
        }

        return new SaveResult(target, ToVaultRelativePath(notePath), mediaRelativePath);
    }

    private async Task<string> SaveMediaAsync(byte[] mediaBytes, string extension, CancellationToken ct)
    {
        var mediaDirectory = ResolveVaultPath(_options.MediaFolderPath);
        Directory.CreateDirectory(mediaDirectory);

        var now = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, _options.TimeZone);
        var cleanExt = NormalizeExtension(extension);
        var baseName = now.ToString("yyyy-MM-dd_HH-mm-ss_fff", CultureInfo.InvariantCulture);
        var suffix = 0;

        while (true)
        {
            var fileName = suffix == 0 ? $"{baseName}{cleanExt}" : $"{baseName}_{suffix}{cleanExt}";
            var absolute = Path.Combine(mediaDirectory, fileName);

            try
            {
                await using var stream = new FileStream(absolute, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
                await stream.WriteAsync(mediaBytes, ct);
                await stream.FlushAsync(ct);
                return ToVaultRelativePath(absolute);
            }
            catch (IOException) when (File.Exists(absolute))
            {
                suffix++;
            }
        }
    }

    private static async Task AppendToFileAsync(string path, string text, CancellationToken ct)
    {
        await using var stream = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read);
        var separator = await GetSeparatorAsync(stream, ct);

        stream.Seek(0, SeekOrigin.End);

        await using var writer = new StreamWriter(stream, new UTF8Encoding(false), leaveOpen: true);
        if (!string.IsNullOrEmpty(separator))
        {
            await writer.WriteAsync(separator.AsMemory(), ct);
        }

        await writer.WriteAsync(text.AsMemory(), ct);
        if (!text.EndsWith('\n'))
        {
            await writer.WriteAsync("\n".AsMemory(), ct);
        }

        await writer.FlushAsync(ct);
        await stream.FlushAsync(ct);
    }

    private static async Task<string> GetSeparatorAsync(FileStream stream, CancellationToken ct)
    {
        if (stream.Length == 0)
        {
            return string.Empty;
        }

        var bytesToRead = (int)Math.Min(4, stream.Length);
        var buffer = new byte[bytesToRead];
        stream.Seek(-bytesToRead, SeekOrigin.End);
        var read = await stream.ReadAsync(buffer.AsMemory(0, bytesToRead), ct);
        var tail = Encoding.UTF8.GetString(buffer, 0, read);
        return tail.EndsWith('\n') ? string.Empty : "\n";
    }

    private string ResolveVaultPath(string configuredPath)
    {
        var combined = Path.IsPathRooted(configuredPath)
            ? configuredPath
            : Path.Combine(_options.VaultPath, configuredPath.Replace('/', Path.DirectorySeparatorChar));

        var full = Path.GetFullPath(combined);
        if (!full.Equals(_options.VaultPath, StringComparison.Ordinal) &&
            !full.StartsWith(_options.VaultPath + Path.DirectorySeparatorChar, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Path must stay inside vault.");
        }

        return full;
    }

    private string ToVaultRelativePath(string fullPath)
    {
        return Path.GetRelativePath(_options.VaultPath, fullPath).Replace('\\', '/');
    }

    private static string NormalizeExtension(string ext)
    {
        var value = string.IsNullOrWhiteSpace(ext) ? ".jpg" : ext.Trim();
        if (!value.StartsWith('.'))
        {
            value = "." + value;
        }

        return value.ToLowerInvariant();
    }

    private static void EnsureMarkdownPath(string path)
    {
        if (!string.Equals(Path.GetExtension(path), ".md", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Target note must be a .md file.");
        }
    }
}