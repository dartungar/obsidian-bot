using Microsoft.Extensions.Configuration;

namespace ObsidianBot.Configuration;

public sealed record ObsidianBotOptions(
    string TelegramBotToken,
    long AllowedUserId,
    string VaultPath,
    string DailyNotesPattern,
    string InboxNotePath,
    string MediaFolderPath,
    TimeZoneInfo TimeZone)
{
    public static ObsidianBotOptions Load(IConfiguration configuration)
    {
        var token = configuration["TELEGRAM_BOT_TOKEN"];
        if (string.IsNullOrWhiteSpace(token))
        {
            throw new InvalidOperationException("TELEGRAM_BOT_TOKEN is required.");
        }

        var userIdRaw = configuration["TELEGRAM_ALLOWED_USER_ID"];
        if (string.IsNullOrWhiteSpace(userIdRaw) || !long.TryParse(userIdRaw, out var allowedUserId))
        {
            throw new InvalidOperationException("TELEGRAM_ALLOWED_USER_ID is required and must be numeric.");
        }

        var timeZoneId = configuration["BUTLER_TIMEZONE"] ?? "UTC";

        return new ObsidianBotOptions(
            TelegramBotToken: token,
            AllowedUserId: allowedUserId,
            VaultPath: Path.GetFullPath(configuration["OBSIDIAN_VAULT_PATH"] ?? "/var/notes"),
            DailyNotesPattern: configuration["OBSIDIAN_DAILY_NOTES_PATTERN"] ?? "04 archive/journal/daily journal/*.md",
            InboxNotePath: configuration["OBSIDIAN_INBOX_NOTE_PATH"] ?? "_inbox/_inbox.md",
            MediaFolderPath: configuration["OBSIDIAN_MEDIA_FOLDER_PATH"] ?? "_inbox",
            TimeZone: ResolveTimeZone(timeZoneId));
    }

    private static TimeZoneInfo ResolveTimeZone(string id)
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(id);
        }
        catch
        {
            return TimeZoneInfo.Utc;
        }
    }
}