using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ObsidianBot.Configuration;

namespace ObsidianBot.Services;

public sealed class VaultSearchIndexer : BackgroundService
{
    private static readonly TimeSpan ChangeDebounce = TimeSpan.FromMilliseconds(750);

    private readonly ILogger<VaultSearchIndexer> _logger;
    private readonly ObsidianBotOptions _options;
    private readonly VaultSearchService _search;
    private readonly SemaphoreSlim _indexRequested = new(0, 1);

    public VaultSearchIndexer(
        ILogger<VaultSearchIndexer> logger,
        ObsidianBotOptions options,
        VaultSearchService search)
    {
        _logger = logger;
        _options = options;
        _search = search;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        Directory.CreateDirectory(_options.VaultPath);
        using var watcher = StartWatcher();

        await UpdateIndexSafelyAsync(stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            var changed = await WaitForChangeOrReconcileAsync(stoppingToken);
            if (stoppingToken.IsCancellationRequested)
            {
                break;
            }

            if (changed)
            {
                await Task.Delay(ChangeDebounce, stoppingToken);
                while (_indexRequested.Wait(0))
                {
                }
            }

            await UpdateIndexSafelyAsync(stoppingToken);
        }
    }

    private FileSystemWatcher StartWatcher()
    {
        var watcher = new FileSystemWatcher(_options.VaultPath, "*.md")
        {
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.FileName |
                           NotifyFilters.LastWrite |
                           NotifyFilters.Size |
                           NotifyFilters.CreationTime,
            EnableRaisingEvents = true
        };
        watcher.Changed += OnNoteChanged;
        watcher.Created += OnNoteChanged;
        watcher.Deleted += OnNoteChanged;
        watcher.Renamed += OnNoteRenamed;
        watcher.Error += OnWatcherError;
        return watcher;
    }

    private async Task<bool> WaitForChangeOrReconcileAsync(CancellationToken ct)
    {
        using var waitCancellation = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var changeTask = _indexRequested.WaitAsync(waitCancellation.Token);
        var reconcileTask = Task.Delay(
            TimeSpan.FromSeconds(_options.SearchReconcileIntervalSeconds),
            ct);
        var completed = await Task.WhenAny(changeTask, reconcileTask);

        if (completed == reconcileTask)
        {
            await waitCancellation.CancelAsync();
            try
            {
                await changeTask;
            }
            catch (OperationCanceledException)
            {
            }

            return false;
        }

        await changeTask;
        return true;
    }

    private async Task UpdateIndexSafelyAsync(CancellationToken ct)
    {
        try
        {
            await _search.UpdateIndexAsync(ct);
            _logger.LogDebug("Search index is current");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to update the search index; will retry");
        }
    }

    private void OnNoteChanged(object sender, FileSystemEventArgs eventArgs)
    {
        RequestIndexUpdate();
    }

    private void OnNoteRenamed(object sender, RenamedEventArgs eventArgs)
    {
        RequestIndexUpdate();
    }

    private void OnWatcherError(object sender, ErrorEventArgs eventArgs)
    {
        _logger.LogWarning(eventArgs.GetException(), "Vault watcher failed; reconciling the search index");
        RequestIndexUpdate();
    }

    private void RequestIndexUpdate()
    {
        try
        {
            _indexRequested.Release();
        }
        catch (SemaphoreFullException)
        {
            // A change is already queued; the next scan compares content hashes.
        }
    }
}
