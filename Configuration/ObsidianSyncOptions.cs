namespace ObsidianBot.Configuration;

public sealed record ObsidianSyncOptions
{
    public bool Enabled { get; init; }
    public string Vault { get; init; } = "";
    public string StateDirectory { get; init; } = "/var/lib/obsidian-bot/sync";
    public string ConfigDirectory { get; init; } = "/var/lib/obsidian-headless";
    public string Executable { get; init; } = "ob";
    public string AuthToken { get; init; } = "";
    public string AuthTokenFile { get; init; } = "";
    public string AccountEmail { get; init; } = "";
    public string AccountPassword { get; init; } = "";
    public string AccountPasswordFile { get; init; } = "";
    public string AccountMfaCode { get; init; } = "";
    public string AccountMfaCodeFile { get; init; } = "";
    public string Password { get; init; } = "";
    public string PasswordFile { get; init; } = "";
    public string DeviceName { get; init; } = "obsidian-bot";
    public bool CreateRemote { get; init; }
    public bool AdoptExisting { get; init; }
    public string Region { get; init; } = "";
    public int IntervalSeconds { get; init; } = 25;
    public int TimeoutSeconds { get; init; } = 180;
    public int InitialTimeoutSeconds { get; init; } = 1800;
    public int StaleAfterSeconds { get; init; } = 300;
    public int MaxBackoffSeconds { get; init; } = 300;
    public int AttentionRetrySeconds { get; init; } = 900;

    public static ObsidianSyncOptions Load(IConfiguration configuration, ObsidianBotOptions bot)
    {
        var options = new ObsidianSyncOptions
        {
            Enabled = bool.TryParse(configuration["OBSIDIAN_SYNC_ENABLED"], out var enabled)
                ? enabled : bot.ComponentRole == "sync",
            Vault = configuration["OBSIDIAN_SYNC_VAULT"]?.Trim() ?? "",
            StateDirectory = Path.GetFullPath(ValueOrDefault(configuration["OBSIDIAN_SYNC_STATE_DIRECTORY"], "/var/lib/obsidian-bot/sync")),
            ConfigDirectory = Path.GetFullPath(ValueOrDefault(configuration["OBSIDIAN_SYNC_CONFIG_DIRECTORY"], "/var/lib/obsidian-headless")),
            Executable = configuration["OBSIDIAN_SYNC_EXECUTABLE"] ?? "ob",
            AuthToken = configuration["OBSIDIAN_AUTH_TOKEN"] ?? "",
            AuthTokenFile = configuration["OBSIDIAN_AUTH_TOKEN_FILE"] ?? "",
            AccountEmail = configuration["OBSIDIAN_ACCOUNT_EMAIL"]?.Trim() ?? "",
            AccountPassword = configuration["OBSIDIAN_ACCOUNT_PASSWORD"] ?? "",
            AccountPasswordFile = configuration["OBSIDIAN_ACCOUNT_PASSWORD_FILE"] ?? "",
            AccountMfaCode = configuration["OBSIDIAN_ACCOUNT_MFA_CODE"]?.Trim() ?? "",
            AccountMfaCodeFile = configuration["OBSIDIAN_ACCOUNT_MFA_CODE_FILE"] ?? "",
            Password = configuration["OBSIDIAN_SYNC_PASSWORD"] ?? "",
            PasswordFile = configuration["OBSIDIAN_SYNC_PASSWORD_FILE"] ?? "",
            DeviceName = configuration["OBSIDIAN_SYNC_DEVICE_NAME"] ?? "obsidian-bot",
            CreateRemote = Boolean(configuration, "OBSIDIAN_SYNC_CREATE_REMOTE"),
            AdoptExisting = Boolean(configuration, "OBSIDIAN_SYNC_ADOPT_EXISTING"),
            Region = configuration["OBSIDIAN_SYNC_REGION"] ?? "",
            IntervalSeconds = Positive(configuration, "OBSIDIAN_SYNC_INTERVAL_SECONDS", 25),
            TimeoutSeconds = Positive(configuration, "OBSIDIAN_SYNC_TIMEOUT_SECONDS", 180),
            InitialTimeoutSeconds = Positive(configuration, "OBSIDIAN_SYNC_INITIAL_TIMEOUT_SECONDS", 1800),
            StaleAfterSeconds = Positive(configuration, "OBSIDIAN_SYNC_STALE_AFTER_SECONDS", 300),
            MaxBackoffSeconds = Positive(configuration, "OBSIDIAN_SYNC_MAX_BACKOFF_SECONDS", 300),
            AttentionRetrySeconds = Positive(configuration, "OBSIDIAN_SYNC_ATTENTION_RETRY_SECONDS", 900)
        };

        if (options.Enabled)
        {
            var ownsSync = bot.ComponentRole is "sync" or "combined";
            var storagePaths = new List<(string Setting, string Path)>
            {
                ("OBSIDIAN_SYNC_STATE_DIRECTORY", options.StateDirectory)
            };
            if (ownsSync) storagePaths.Add(("OBSIDIAN_SYNC_CONFIG_DIRECTORY", options.ConfigDirectory));
            if (bot.RunsSearchIndexer) storagePaths.Add(("OBSIDIAN_SEARCH_DATABASE_PATH", bot.SearchDatabasePath));
            if (bot.RunsAgentApi || bot.RunsPublisher)
            {
                storagePaths.Add(("OBSIDIAN_PROPOSAL_DATABASE_PATH", bot.ProposalDatabasePath));
                storagePaths.Add(("OBSIDIAN_PROPOSAL_SNAPSHOT_DIRECTORY", bot.ProposalSnapshotDirectory));
            }
            foreach (var (setting, path) in storagePaths)
            {
                if (IsWithin(path, bot.VaultPath))
                    throw new InvalidOperationException(
                        $"{setting} resolves to '{path}', inside OBSIDIAN_VAULT_PATH '{bot.VaultPath}'. " +
                        "Managed sync requires this state outside the vault; use /var/lib/obsidian-bot or /var/lib/obsidian-headless.");
            }
            if (ownsSync && string.IsNullOrWhiteSpace(options.Vault))
                throw new InvalidOperationException("OBSIDIAN_SYNC_VAULT is required for the sync owner.");
            if (ownsSync && (IsWithin(options.ConfigDirectory, options.StateDirectory) || IsWithin(options.StateDirectory, options.ConfigDirectory)))
                throw new InvalidOperationException("Sync status and private headless configuration must use separate directories.");
        }
        return options;
    }

    public string ReadAuthToken() => ReadSecret(AuthTokenFile, AuthToken);
    public bool HasExplicitAuthToken => !string.IsNullOrWhiteSpace(AuthToken) || !string.IsNullOrWhiteSpace(AuthTokenFile);
    public string ReadAccountPassword() => ReadSecret(AccountPasswordFile, AccountPassword);
    public string ReadAccountMfaCode() => ReadSecret(AccountMfaCodeFile, AccountMfaCode);
    public string ReadPassword() => ReadSecret(PasswordFile, Password);

    private static string ReadSecret(string file, string fallback)
    {
        if (string.IsNullOrEmpty(file)) return fallback;
        var value = File.ReadAllText(file).TrimEnd('\r', '\n');
        return value.Length > 0 ? value : throw new InvalidOperationException("A configured sync secret file is empty.");
    }

    private static bool IsWithin(string path, string root) =>
        Path.GetFullPath(path).Equals(Path.GetFullPath(root), StringComparison.Ordinal) ||
        Path.GetFullPath(path).StartsWith(Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)) + Path.DirectorySeparatorChar, StringComparison.Ordinal);

    private static bool Boolean(IConfiguration configuration, string key) =>
        bool.TryParse(configuration[key], out var value) && value;

    private static string ValueOrDefault(string? value, string fallback) =>
        string.IsNullOrWhiteSpace(value) ? fallback : value;

    private static int Positive(IConfiguration configuration, string key, int fallback)
    {
        if (string.IsNullOrWhiteSpace(configuration[key])) return fallback;
        var raw = configuration[key];
        return int.TryParse(raw, out var value) && value > 0 && value <= 86400
            ? value : throw new InvalidOperationException($"{key} must be between 1 and 86400.");
    }
}
