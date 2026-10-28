using System.Collections.Concurrent;

namespace ObsidianBot.Services;

public sealed class CrossProcessFileLease : IAsyncDisposable
{
    private readonly FileStream _stream;
    private readonly SemaphoreSlim _localLock;
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Locks = new(StringComparer.Ordinal);
    private CrossProcessFileLease(FileStream stream, SemaphoreSlim localLock) => (_stream, _localLock) = (stream, localLock);

    public static async Task<CrossProcessFileLease> AcquireAsync(string path, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var localLock = Locks.GetOrAdd(Path.GetFullPath(path), _ => new SemaphoreSlim(1, 1));
        await localLock.WaitAsync(ct);
        try
        {
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    return new(new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite,
                        FileShare.None, 1, FileOptions.Asynchronous), localLock);
                }
                catch (IOException e) when ((e.HResult & 0xffff) is 11 or 32 or 33)
                {
                    await Task.Delay(100, ct);
                }
            }
        }
        catch { localLock.Release(); throw; }
    }

    public async ValueTask DisposeAsync()
    {
        try { await _stream.DisposeAsync(); }
        finally { _localLock.Release(); }
    }
}
