using System.Text.Json;
using ObsidianBot.Configuration;
using ObsidianBot.Models;

namespace ObsidianBot.Services;

public sealed class VaultSyncStateStore(ObsidianSyncOptions options, ObsidianBotOptions bot)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly DateTimeOffset _firstObservedAt = DateTimeOffset.UtcNow;
    public string StatusPath => Path.Combine(options.StateDirectory, "status.json");
    public string BootstrapPath => Path.Combine(options.StateDirectory, "bootstrap.json");
    public string LocalIdentityPath => Path.Combine(bot.VaultPath, ".obsidian-bot", "sync-instance");

    public VaultSyncStatus Read()
    {
        if (!options.Enabled) return new() { State = "disabled", Initialized = true };
        VaultSyncStatus? status = null;
        try
        {
            status = JsonSerializer.Deserialize<VaultSyncStatus>(File.ReadAllText(StatusPath), JsonOptions);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { }
        if (status is null || status.VaultPath != bot.VaultPath)
            status = new() { VaultPath = bot.VaultPath, UpdatedAt = _firstObservedAt };

        var allowedAge = status.IsRunning
            ? Math.Max(options.StaleAfterSeconds, status.ActiveTimeoutSeconds + 30)
            : Math.Max(options.StaleAfterSeconds,
                (status.NextAttemptAt - status.UpdatedAt)?.TotalSeconds + Math.Min(30, options.StaleAfterSeconds) ?? 0);
        if (DateTimeOffset.UtcNow - status.UpdatedAt > TimeSpan.FromSeconds(allowedAge))
            status = status with { State = "stale", IsRunning = false,
                ErrorCode = "SYNC_OWNER_STALE", ErrorMessage = "The sync service has stopped reporting progress." };
        if (status.Initialized && status.LastSuccessfulSyncAt is { } successfulAt &&
            DateTimeOffset.UtcNow - successfulAt > TimeSpan.FromSeconds(options.StaleAfterSeconds) &&
            status.State is "healthy" or "syncing")
            status = status with { State = "degraded", ErrorCode = "SYNC_SUCCESS_STALE",
                ErrorMessage = "No sync round has completed successfully within the configured health window." };
        if (status.Initialized)
        {
            try
            {
                var identity = JsonSerializer.Deserialize<SyncBootstrapState>(File.ReadAllText(BootstrapPath), JsonOptions);
                if (identity is null || identity.VaultId != status.VaultId || !LocalIdentityMatches(identity))
                    status = status with { State = "needs-attention", Initialized = false,
                        ErrorCode = "SYNC_LOCAL_VAULT_CHANGED", ErrorMessage = "The local vault identity is missing or changed. Restore its volume before resuming sync." };
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
            {
                status = status with { State = "needs-attention", Initialized = false,
                    ErrorCode = "SYNC_LOCAL_VAULT_CHANGED", ErrorMessage = "The local vault identity cannot be verified. Restore its volume before resuming sync." };
            }
        }
        if (!Directory.Exists(bot.VaultPath)) status = status with { Initialized = false };
        return status;
    }

    public bool LocalIdentityMatches(SyncBootstrapState identity)
    {
        try
        {
            return !string.IsNullOrEmpty(identity.LocalInstanceId) && identity.VaultPath == bot.VaultPath &&
                File.ReadAllText(LocalIdentityPath) == identity.LocalInstanceId;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return false; }
    }

    public void EnsureLocalIdentity(SyncBootstrapState identity)
    {
        if (!LocalIdentityMatches(identity))
            throw new SyncFailure("SYNC_LOCAL_VAULT_CHANGED",
                "The local vault identity is missing or changed. Restore its volume before resuming sync.", true);
    }

    public async Task WriteLocalIdentityAsync(SyncBootstrapState identity, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(LocalIdentityPath)!);
        await File.WriteAllTextAsync(LocalIdentityPath, identity.LocalInstanceId, ct);
    }

    public bool IsReady => !options.Enabled || Read().Initialized;

    public void EnsureReady()
    {
        if (!IsReady) throw new InvalidOperationException("The vault is waiting for its initial sync. Check /syncstatus.");
    }

    public async Task WaitUntilReadyAsync(CancellationToken ct)
    {
        while (!IsReady) await Task.Delay(TimeSpan.FromSeconds(1), ct);
    }

    public Task WriteAsync(VaultSyncStatus status, CancellationToken ct) =>
        WriteJsonAsync(StatusPath, status with { UpdatedAt = DateTimeOffset.UtcNow }, ct);

    public async Task<SyncBootstrapState?> ReadBootstrapAsync(CancellationToken ct)
    {
        if (!File.Exists(BootstrapPath)) return null;
        // Corrupt identity state must not silently relink the vault.
        return JsonSerializer.Deserialize<SyncBootstrapState>(await File.ReadAllTextAsync(BootstrapPath, ct), JsonOptions)
            ?? throw new JsonException("Invalid sync bootstrap state.");
    }

    public Task WriteBootstrapAsync(SyncBootstrapState state, CancellationToken ct) => WriteJsonAsync(BootstrapPath, state, ct);

    public static async Task WriteJsonAsync<T>(string path, T value, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(value, JsonOptions), ct);
            File.Move(temporary, path, overwrite: true);
        }
        finally { File.Delete(temporary); }
    }
}

public sealed record SyncBootstrapState(string Target, string VaultId, string VaultPath, bool Initialized, string LocalInstanceId);
