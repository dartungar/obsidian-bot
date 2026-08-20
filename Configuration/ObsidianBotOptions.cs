using Microsoft.Extensions.Configuration;

namespace ObsidianBot.Configuration;

public sealed record ObsidianBotOptions(
    string TelegramBotToken,
    long AllowedUserId,
    string VaultPath,
    string DailyNotesPattern,
    string InboxNotePath,
    string MediaFolderPath,
    string SearchDatabasePath,
    string EmbeddingsApiKey,
    Uri EmbeddingsApiUrl,
    string EmbeddingModel,
    int EmbeddingDimensions,
    int SearchResultLimit,
    int SearchReconcileIntervalSeconds,
    string ApiToken,
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

        var vaultPath = Path.GetFullPath(configuration["OBSIDIAN_VAULT_PATH"] ?? "/var/notes");

        return new ObsidianBotOptions(
            TelegramBotToken: token,
            AllowedUserId: allowedUserId,
            VaultPath: vaultPath,
            DailyNotesPattern: configuration["OBSIDIAN_DAILY_NOTES_PATTERN"] ?? "04 archive/journal/daily journal/*.md",
            InboxNotePath: configuration["OBSIDIAN_INBOX_NOTE_PATH"] ?? "_inbox/_inbox.md",
            MediaFolderPath: configuration["OBSIDIAN_MEDIA_FOLDER_PATH"] ?? "_inbox",
            SearchDatabasePath: ResolveVaultPath(
                vaultPath,
                configuration["OBSIDIAN_SEARCH_DATABASE_PATH"] ?? ".obsidian-bot/search.db"),
            EmbeddingsApiKey: configuration["OPENAI_API_KEY"] ?? string.Empty,
            EmbeddingsApiUrl: ParseUri(
                configuration["OPENAI_EMBEDDINGS_URL"],
                "https://api.openai.com/v1/embeddings"),
            EmbeddingModel: configuration["OPENAI_EMBEDDING_MODEL"] ?? "text-embedding-3-small",
            EmbeddingDimensions: ParsePositiveInt(configuration["OPENAI_EMBEDDING_DIMENSIONS"], 1536),
            SearchResultLimit: ParsePositiveInt(configuration["OBSIDIAN_SEARCH_RESULT_LIMIT"], 5),
            SearchReconcileIntervalSeconds: ParsePositiveInt(
                configuration["OBSIDIAN_SEARCH_RECONCILE_INTERVAL_SECONDS"],
                60),
            ApiToken: configuration["OBSIDIAN_API_TOKEN"] ?? string.Empty,
            TimeZone: ResolveTimeZone(timeZoneId));
    }

    private static string ResolveVaultPath(string vaultPath, string configuredPath)
    {
        var combined = Path.IsPathRooted(configuredPath)
            ? configuredPath
            : Path.Combine(vaultPath, configuredPath.Replace('/', Path.DirectorySeparatorChar));
        var fullPath = Path.GetFullPath(combined);

        if (!fullPath.StartsWith(vaultPath + Path.DirectorySeparatorChar, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Search database path must stay inside the vault.");
        }

        return fullPath;
    }

    private static Uri ParseUri(string? value, string fallback)
    {
        if (!Uri.TryCreate(value ?? fallback, UriKind.Absolute, out var uri))
        {
            throw new InvalidOperationException("OPENAI_EMBEDDINGS_URL must be an absolute URL.");
        }

        return uri;
    }

    private static int ParsePositiveInt(string? value, int fallback)
    {
        return int.TryParse(value, out var parsed) && parsed > 0 ? parsed : fallback;
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
