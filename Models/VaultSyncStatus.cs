namespace ObsidianBot.Models;

public sealed record VaultSyncStatus
{
    public string State { get; init; } = "initializing";
    public bool Initialized { get; init; }
    public string VaultPath { get; init; } = "";
    public string? VaultId { get; init; }
    public DateTimeOffset UpdatedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? LastAttemptAt { get; init; }
    public DateTimeOffset? LastSuccessfulSyncAt { get; init; }
    public DateTimeOffset? NextAttemptAt { get; init; }
    public double? LastDurationSeconds { get; init; }
    public int ConsecutiveFailures { get; init; }
    public bool IsRunning { get; init; }
    public int ActiveTimeoutSeconds { get; init; }
    public string? ErrorCode { get; init; }
    public string? ErrorMessage { get; init; }

    public string ToTelegramText() => State == "disabled"
        ? "Managed vault sync is disabled."
        : $"Vault sync: {State}\n" +
          $"Local vault: {(Initialized ? "available" : "waiting for initial sync")}\n" +
          $"Last successful sync: {(LastSuccessfulSyncAt is { } at ? at.ToString("u") : "none")}\n" +
          $"Consecutive failures: {ConsecutiveFailures}" +
          (ErrorMessage is null ? "" : $"\n{ErrorMessage}");
}
