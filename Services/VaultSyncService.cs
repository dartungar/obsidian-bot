using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using ObsidianBot.Configuration;
using ObsidianBot.Models;

namespace ObsidianBot.Services;

public sealed class VaultSyncService(
    ObsidianSyncOptions options, ObsidianBotOptions bot, VaultSyncStateStore store,
    VaultBootstrapService bootstrap, SyncProcessClient client, ILogger<VaultSyncService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // A lifetime lease prevents duplicate owners, including overlapping container deployments.
        await using var owner = await CrossProcessFileLease.AcquireAsync(
            Path.Combine(options.StateDirectory, "owner.lock"), stoppingToken);
        Directory.CreateDirectory(options.ConfigDirectory);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(options.ConfigDirectory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var status = new VaultSyncStatus { VaultPath = bot.VaultPath };
        SyncBootstrapState? identity = null;
        var configured = false;
        try
        {
            identity = await store.ReadBootstrapAsync(stoppingToken);
            if (identity is not null && identity.Target == options.Vault && identity.VaultPath == bot.VaultPath && Directory.Exists(bot.VaultPath))
            {
                status = status with { Initialized = identity.Initialized && store.LocalIdentityMatches(identity), VaultId = identity.VaultId,
                    LastSuccessfulSyncAt = store.Read().LastSuccessfulSyncAt };
            }
        }
        catch (Exception e) when (e is JsonException or IOException) { /* Report safely in the retry loop. */ }

        while (!stoppingToken.IsCancellationRequested)
        {
            var timeout = status.Initialized ? options.TimeoutSeconds : options.InitialTimeoutSeconds;
            status = status with { State = status.Initialized ? "syncing" : "initializing", IsRunning = true,
                ActiveTimeoutSeconds = timeout, LastAttemptAt = DateTimeOffset.UtcNow, NextAttemptAt = null };
            await store.WriteAsync(status, stoppingToken);
            var timer = Stopwatch.StartNew();
            SyncFailure? failure = null;
            try
            {
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                deadline.CancelAfter(TimeSpan.FromSeconds(timeout));
                await using var vault = await CrossProcessFileLease.AcquireAsync(
                    Path.Combine(options.StateDirectory, "vault.lock"), deadline.Token);
                if (!configured)
                {
                    identity = await bootstrap.ConfigureAsync(deadline.Token);
                    status = status with { VaultId = identity.VaultId };
                    configured = true;
                }
                store.EnsureLocalIdentity(identity!);
                var result = await client.RunAsync(["sync", "--path", bot.VaultPath], timeout, deadline.Token);
                result.EnsureSuccess(requireCompletion: true);
                if (!status.Initialized) bootstrap.PrepareWritableFolders();
                identity = identity! with { Initialized = true };
                await store.WriteBootstrapAsync(identity, deadline.Token);
                status = status with { State = "healthy", Initialized = true, ConsecutiveFailures = 0,
                    LastSuccessfulSyncAt = DateTimeOffset.UtcNow, ErrorCode = null, ErrorMessage = null };
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (OperationCanceledException)
            {
                failure = new("SYNC_TIMEOUT", "The sync round timed out and was terminated.");
            }
            catch (SyncFailure e) { failure = e; }
            catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException)
            {
                failure = new("SYNC_CONFIGURATION", "The vault configuration or headless response is invalid. Check the configured vault and pinned client version.", true);
            }
            catch (Win32Exception)
            {
                failure = new("SYNC_EXECUTABLE", "The headless client could not be started. Check OBSIDIAN_SYNC_EXECUTABLE.", true);
            }
            catch (Exception)
            {
                failure = new("SYNC_STORAGE", "Sync could not access its storage or secret files. Check mounts, permissions, and disk space.");
            }

            if (failure is not null)
            {
                status = status with { State = failure.NeedsAttention ? "needs-attention" : "degraded",
                    ConsecutiveFailures = status.ConsecutiveFailures + 1, ErrorCode = failure.Code, ErrorMessage = failure.Message };
                if (failure.Code == "SYNC_LOCAL_VAULT_CHANGED") status = status with { Initialized = false };
                logger.LogWarning("Vault sync failed ({Code}); consecutive failures: {Count}", failure.Code, status.ConsecutiveFailures);
                // Retry setup for configuration/authentication failures; network errors during a round need no relink.
                if (failure.NeedsAttention) configured = false;
            }
            else logger.LogInformation("Vault sync completed in {Seconds:F1}s", timer.Elapsed.TotalSeconds);

            var delay = failure?.NeedsAttention == true ? options.AttentionRetrySeconds :
                failure is null ? options.IntervalSeconds :
                Math.Max(options.IntervalSeconds, (int)Math.Min(options.MaxBackoffSeconds,
                    options.IntervalSeconds * Math.Pow(2, Math.Min(status.ConsecutiveFailures - 1, 16))));
            status = status with { IsRunning = false, LastDurationSeconds = timer.Elapsed.TotalSeconds,
                NextAttemptAt = DateTimeOffset.UtcNow.AddSeconds(delay) };
            await store.WriteAsync(status, stoppingToken);
            await Task.Delay(TimeSpan.FromSeconds(delay), stoppingToken);
        }
    }
}
