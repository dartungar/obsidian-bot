using System.Collections.Concurrent;

namespace ObsidianBot.Services;

public static class VaultWriteLock
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Locks = new(StringComparer.Ordinal);

    public static async Task<VaultWriteLease> AcquireAsync(string path, CancellationToken ct)
    {
        var localLock = Locks.GetOrAdd(path, static _ => new SemaphoreSlim(1, 1));
        await localLock.WaitAsync(ct);
        try
        {
            var lockPath = path + ".obsidian-bot.lock";
            Directory.CreateDirectory(Path.GetDirectoryName(lockPath) ?? ".");
            while (true)
            {
                FileStream? stream = null;
                try
                {
                    stream = new FileStream(
                        lockPath,
                        FileMode.OpenOrCreate,
                        FileAccess.ReadWrite,
                        FileShare.None,
                        bufferSize: 1,
                        FileOptions.Asynchronous);
                    return new VaultWriteLease(localLock, stream);
                }
                catch (IOException)
                {
                    if (stream is not null)
                    {
                        await stream.DisposeAsync();
                    }

                    await Task.Delay(TimeSpan.FromMilliseconds(50), ct);
                }
            }
        }
        catch
        {
            localLock.Release();
            throw;
        }
    }
}

public sealed class VaultWriteLease : IAsyncDisposable
{
    private readonly SemaphoreSlim _localLock;
    private readonly FileStream _stream;

    internal VaultWriteLease(SemaphoreSlim localLock, FileStream stream)
    {
        _localLock = localLock;
        _stream = stream;
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await _stream.DisposeAsync();
        }
        finally
        {
            _localLock.Release();
        }
    }
}
