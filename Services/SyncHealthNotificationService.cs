using System.Text.Json;
using ObsidianBot.Configuration;
using Telegram.Bot;

namespace ObsidianBot.Services;

public sealed class SyncHealthNotificationService(
    VaultSyncStateStore store, ObsidianSyncOptions options, ObsidianBotOptions bot,
    ITelegramBotClient telegram, ILogger<SyncHealthNotificationService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var path = Path.Combine(options.StateDirectory, "telegram-alert.json");
        var incidentOpen = false;
        var stateDirty = false;
        try
        {
            if (File.Exists(path)) incidentOpen = JsonSerializer.Deserialize<bool>(await File.ReadAllTextAsync(path, stoppingToken));
        }
        catch (Exception e) when (e is IOException or JsonException) { }
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var status = store.Read();
                var unhealthy = status.State is "needs-attention" or "stale" || status.ConsecutiveFailures >= 3 ||
                    status.ErrorCode == "SYNC_SUCCESS_STALE";
                var recovered = status.State == "healthy";
                if ((unhealthy && !incidentOpen) || (recovered && incidentOpen))
                {
                    await telegram.SendTextMessageAsync(bot.AllowedUserId,
                        (unhealthy ? "Obsidian sync needs attention.\n" : "Obsidian sync recovered.\n") + status.ToTelegramText(),
                        cancellationToken: stoppingToken);
                    incidentOpen = unhealthy;
                    stateDirty = true;
                }
                if (stateDirty)
                {
                    await VaultSyncStateStore.WriteJsonAsync(path, incidentOpen, stoppingToken);
                    stateDirty = false;
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception) { logger.LogWarning("Unable to deliver or persist the sync health notification; will retry."); }
            await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
        }
    }
}
