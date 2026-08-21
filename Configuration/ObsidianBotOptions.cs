using Microsoft.Extensions.Configuration;

namespace ObsidianBot.Configuration;

public sealed record ObsidianBotOptions(
    string ComponentRole,
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
    string AgentApiToken,
    string ReviewApiToken,
    string ProposalDatabasePath,
    string ProposalSnapshotDirectory,
    IReadOnlyList<string> AgentReadableFolders,
    IReadOnlyList<string> AgentWritableFolders,
    IReadOnlyList<string> AgentDeniedFolders,
    IReadOnlyList<string> AgentDirectAllowedHeadings,
    int DirectChangeMaxContentBytes,
    TimeSpan DirectChangeUndoWindow,
    TimeSpan ProposalTtl,
    int ProposalMaxMarkdownLength,
    int PublisherPollIntervalSeconds,
    TimeZoneInfo TimeZone)
{
    public bool RunsTelegram => ComponentRole is "combined" or "telegram";

    public bool RunsAgentApi => ComponentRole is "combined" or "agent-api";

    public bool RunsSearchIndexer => RunsTelegram || RunsAgentApi;

    public bool RunsPublisher => ComponentRole == "publisher" ||
                                 (ComponentRole == "combined" && PublisherEnabled);

    public bool PublisherEnabled { get; init; }

    public static ObsidianBotOptions Load(IConfiguration configuration)
    {
        var componentRole = (configuration["OBSIDIAN_COMPONENT_ROLE"] ?? "combined").Trim().ToLowerInvariant();
        if (componentRole is not ("combined" or "telegram" or "agent-api" or "publisher"))
        {
            throw new InvalidOperationException(
                "OBSIDIAN_COMPONENT_ROLE must be one of: combined, telegram, agent-api, publisher.");
        }

        var token = configuration["TELEGRAM_BOT_TOKEN"] ?? string.Empty;
        var userIdRaw = configuration["TELEGRAM_ALLOWED_USER_ID"];
        var allowedUserId = 0L;
        if (componentRole is "combined" or "telegram")
        {
            if (string.IsNullOrWhiteSpace(token))
            {
                throw new InvalidOperationException("TELEGRAM_BOT_TOKEN is required when Telegram is enabled.");
            }

            if (string.IsNullOrWhiteSpace(userIdRaw) || !long.TryParse(userIdRaw, out allowedUserId))
            {
                throw new InvalidOperationException("TELEGRAM_ALLOWED_USER_ID is required and must be numeric when Telegram is enabled.");
            }
        }

        var timeZoneId = configuration["BUTLER_TIMEZONE"] ?? "UTC";

        var vaultPath = Path.GetFullPath(configuration["OBSIDIAN_VAULT_PATH"] ?? "/var/notes");

        var searchDatabasePath = ResolveStoragePath(
            vaultPath,
            configuration["OBSIDIAN_SEARCH_DATABASE_PATH"] ?? ".obsidian-bot/search.db");
        var proposalDatabasePath = ResolveStoragePath(
            vaultPath,
            configuration["OBSIDIAN_PROPOSAL_DATABASE_PATH"] ?? ".obsidian-bot/proposals.db");
        var proposalSnapshotDirectory = ResolveStoragePath(
            vaultPath,
            configuration["OBSIDIAN_PROPOSAL_SNAPSHOT_DIRECTORY"] ?? ".obsidian-bot/snapshots");

        return new ObsidianBotOptions(
            ComponentRole: componentRole,
            TelegramBotToken: token,
            AllowedUserId: allowedUserId,
            VaultPath: vaultPath,
            DailyNotesPattern: configuration["OBSIDIAN_DAILY_NOTES_PATTERN"] ?? "04 archive/journal/daily journal/*.md",
            InboxNotePath: configuration["OBSIDIAN_INBOX_NOTE_PATH"] ?? "_inbox/_inbox.md",
            MediaFolderPath: configuration["OBSIDIAN_MEDIA_FOLDER_PATH"] ?? "_inbox",
            SearchDatabasePath: searchDatabasePath,
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
            AgentApiToken: configuration["OBSIDIAN_AGENT_API_TOKEN"] ?? string.Empty,
            ReviewApiToken: configuration["OBSIDIAN_REVIEW_API_TOKEN"] ?? string.Empty,
            ProposalDatabasePath: proposalDatabasePath,
            ProposalSnapshotDirectory: proposalSnapshotDirectory,
            AgentReadableFolders: ParsePathList(configuration["OBSIDIAN_AGENT_READABLE_FOLDERS"]),
            AgentWritableFolders: ParsePathList(configuration["OBSIDIAN_AGENT_WRITABLE_FOLDERS"], "_inbox"),
            AgentDeniedFolders: ParsePathList(
                configuration["OBSIDIAN_AGENT_DENIED_FOLDERS"],
                ".obsidian,.git,attachments,templates,04 archive"),
            AgentDirectAllowedHeadings: ParseValueList(
                configuration["OBSIDIAN_AGENT_DIRECT_ALLOWED_HEADINGS"],
                "Notes,Decisions,Tasks,Next Steps,Journal,Agent Capture"),
            DirectChangeMaxContentBytes: ParsePositiveInt(
                configuration["OBSIDIAN_DIRECT_CHANGE_MAX_CONTENT_BYTES"],
                25_600),
            DirectChangeUndoWindow: TimeSpan.FromSeconds(ParsePositiveInt(
                configuration["OBSIDIAN_DIRECT_CHANGE_UNDO_WINDOW_SECONDS"],
                86_400)),
            ProposalTtl: TimeSpan.FromHours(ParsePositiveInt(configuration["OBSIDIAN_PROPOSAL_TTL_HOURS"], 24)),
            ProposalMaxMarkdownLength: ParsePositiveInt(configuration["OBSIDIAN_PROPOSAL_MAX_MARKDOWN_LENGTH"], 20_000),
            PublisherPollIntervalSeconds: ParsePositiveInt(
                configuration["OBSIDIAN_PUBLISHER_POLL_INTERVAL_SECONDS"],
                2),
            TimeZone: ResolveTimeZone(timeZoneId))
        {
            PublisherEnabled = ParseBoolean(configuration["OBSIDIAN_PUBLISHER_ENABLED"])
        };
    }

    private static string ResolveStoragePath(string vaultPath, string configuredPath)
    {
        var combined = Path.IsPathRooted(configuredPath)
            ? configuredPath
            : Path.Combine(vaultPath, configuredPath.Replace('/', Path.DirectorySeparatorChar));
        var fullPath = Path.GetFullPath(combined);

        return fullPath;
    }

    private static IReadOnlyList<string> ParsePathList(string? value, string fallback = "")
    {
        return (value ?? fallback)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(path => path.Replace('\\', '/').Trim('/'))
            .Where(path => !string.IsNullOrWhiteSpace(path) && path is not "." and not "..")
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static IReadOnlyList<string> ParseValueList(string? value, string fallback) =>
        (value ?? fallback)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

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

    private static bool ParseBoolean(string? value) =>
        bool.TryParse(value, out var parsed) && parsed;

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
