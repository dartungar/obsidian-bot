using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using ObsidianBot.Configuration;

namespace ObsidianBot.Services;

public static class VaultWriteLock
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Locks = new(StringComparer.Ordinal);
    private static string? _stateDirectory;
    private static VaultSyncStateStore? _syncState;

    public static void Configure(ObsidianSyncOptions options, VaultSyncStateStore syncState)
    {
        _stateDirectory = options.Enabled ? options.StateDirectory : null;
        _syncState = options.Enabled ? syncState : null;
    }

    public static async Task<VaultWriteLease> AcquireAsync(string path, CancellationToken ct)
    {
        _syncState?.EnsureReady();
        var vaultLease = _stateDirectory is null ? null : await CrossProcessFileLease.AcquireAsync(
            Path.Combine(_stateDirectory, "vault.lock"), ct);
        try
        {
            _syncState?.EnsureReady();
            return await AcquireFileAsync(path, vaultLease, ct);
        }
        catch
        {
            if (vaultLease is not null) await vaultLease.DisposeAsync();
            throw;
        }
    }

    private static async Task<VaultWriteLease> AcquireFileAsync(string path, CrossProcessFileLease? vaultLease, CancellationToken ct)
    {
        var localLock = Locks.GetOrAdd(path, static _ => new SemaphoreSlim(1, 1));
        await localLock.WaitAsync(ct);
        try
        {
            var lockPath = _stateDirectory is null ? path + ".obsidian-bot.lock" :
                Path.Combine(_stateDirectory, "locks", Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(path)))) + ".lock");
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
                    return new VaultWriteLease(localLock, stream, vaultLease);
                }
                catch (IOException e) when ((e.HResult & 0xffff) is 11 or 32 or 33)
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
    private readonly CrossProcessFileLease? _vaultLease;

    internal VaultWriteLease(SemaphoreSlim localLock, FileStream stream, CrossProcessFileLease? vaultLease)
    {
        _localLock = localLock;
        _stream = stream;
        _vaultLease = vaultLease;
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
            if (_vaultLease is not null) await _vaultLease.DisposeAsync();
        }
    }
}
