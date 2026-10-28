using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ObsidianBot.Configuration;

namespace ObsidianBot.Services;

public sealed class VaultBootstrapService(
    ObsidianSyncOptions options, ObsidianBotOptions bot,
    SyncProcessClient client, VaultSyncStateStore store)
{
    public void PrepareWritableFolders()
    {
        var policy = new VaultAccessPolicy(bot);
        foreach (var configured in bot.AgentWritableFolders)
        {
            if (!policy.CanWrite(configured)) continue;
            var full = Path.GetFullPath(Path.Combine(bot.VaultPath, configured));
            var relative = Path.GetRelativePath(bot.VaultPath, full);
            if (relative == ".." || relative.StartsWith("../", StringComparison.Ordinal) || Path.IsPathRooted(relative))
                throw new SyncFailure("SYNC_WRITABLE_FOLDER", "A configured writable folder is outside the vault.", true);
            var current = bot.VaultPath;
            foreach (var component in relative.Split(Path.DirectorySeparatorChar))
            {
                current = Path.Combine(current, component);
                if (Path.Exists(current) && File.GetAttributes(current).HasFlag(FileAttributes.ReparsePoint))
                    throw new SyncFailure("SYNC_VAULT_SYMLINK", "A configured writable folder contains symbolic links.", true);
                Directory.CreateDirectory(current);
            }
        }
    }

    public async Task<SyncBootstrapState> ConfigureAsync(CancellationToken ct)
    {
        var saved = await store.ReadBootstrapAsync(ct);
        if (saved is not null && (saved.Target != options.Vault || saved.VaultPath != bot.VaultPath))
            throw new SyncFailure("SYNC_IDENTITY_CHANGED", "The configured vault differs from the saved identity. Migrate it explicitly before syncing.", true);
        if (saved is not null) store.EnsureLocalIdentity(saved);

        var listing = await client.RunAsync(["sync-list-remote", "--json"], 60, ct);
        listing.EnsureSuccess();
        var matches = FindVaults(listing.Output, saved?.VaultId ?? options.Vault);
        if (matches.Count == 0 && saved is null && options.CreateRemote)
        {
            var password = options.ReadPassword();
            if (string.IsNullOrEmpty(password))
                throw new SyncFailure("SYNC_PASSWORD_REQUIRED", "Creating an encrypted vault requires OBSIDIAN_SYNC_PASSWORD or its secret file.", true);
            var create = new List<string> { "sync-create-remote", "--name", options.Vault,
                "--encryption", "end-to-end", "--password", password };
            if (!string.IsNullOrWhiteSpace(options.Region)) create.AddRange(["--region", options.Region]);
            (await client.RunAsync(create, 60, ct)).EnsureSuccess();
            listing = await client.RunAsync(["sync-list-remote", "--json"], 60, ct);
            listing.EnsureSuccess();
            matches = FindVaults(listing.Output, options.Vault);
        }
        if (matches.Count != 1)
            throw new SyncFailure("SYNC_VAULT_SELECTION", "The remote vault is missing or its name is ambiguous. Configure an accessible vault ID.", true);
        var vaultId = matches[0];
        if (saved is not null && saved.VaultId != vaultId)
            throw new SyncFailure("SYNC_IDENTITY_CHANGED", "The remote vault identity changed. Automatic relinking was refused.", true);

        var existing = await client.RunAsync(["sync-status", "--path", bot.VaultPath, "--json"], 60, ct);
        if (existing.ExitCode == 0)
        {
            using var json = JsonDocument.Parse(existing.Output);
            if (json.RootElement.GetProperty("vaultId").GetString() != vaultId ||
                json.RootElement.GetProperty("vaultPath").GetString() != bot.VaultPath)
                throw new SyncFailure("SYNC_IDENTITY_CHANGED", "The local headless configuration belongs to another vault. Automatic relinking was refused.", true);
        }
        else if (existing.ExitCode != 3)
        {
            existing.EnsureSuccess();
        }

        if (saved is null && Directory.Exists(bot.VaultPath) && Directory.EnumerateFileSystemEntries(bot.VaultPath).Any())
        {
            if (!options.AdoptExisting)
                throw new SyncFailure("SYNC_EXISTING_VAULT", "The local vault is not empty. Enable OBSIDIAN_SYNC_ADOPT_EXISTING to back it up and adopt it.", true);
            await BackUpAsync(ct);
        }
        Directory.CreateDirectory(bot.VaultPath);
        if (existing.ExitCode == 3)
        {
            var setup = new List<string> { "sync-setup", "--vault", vaultId, "--path", bot.VaultPath,
                "--device-name", options.DeviceName, "--json" };
            var password = options.ReadPassword();
            if (!string.IsNullOrEmpty(password)) setup.AddRange(["--password", password]);
            (await client.RunAsync(setup, 60, ct)).EnsureSuccess();
        }
        (await client.RunAsync(["sync-config", "--path", bot.VaultPath,
            "--mode", "bidirectional", "--conflict-strategy", "conflict",
            "--file-types", "image,audio,video,pdf", "--configs", "",
            "--excluded-folders", ".obsidian-bot,.git", "--device-name", options.DeviceName, "--json"], 60, ct)).EnsureSuccess();

        var state = saved ?? new SyncBootstrapState(options.Vault, vaultId, bot.VaultPath, false, Guid.NewGuid().ToString("N"));
        if (saved is null) await store.WriteLocalIdentityAsync(state, ct);
        await store.WriteBootstrapAsync(state, ct);
        return state;
    }

    private static List<string> FindVaults(string output, string target)
    {
        using var json = JsonDocument.Parse(output);
        var vaults = new List<(string Id, string? Name)>();
        foreach (var key in new[] { "vaults", "shared" })
            if (json.RootElement.TryGetProperty(key, out var array))
                foreach (var vault in array.EnumerateArray())
                    vaults.Add((vault.GetProperty("id").GetString()!, vault.GetProperty("name").GetString()));
        var byId = vaults.Where(vault => vault.Id == target).Select(vault => vault.Id).Distinct().ToList();
        return byId.Count > 0 ? byId : vaults.Where(vault => vault.Name == target).Select(vault => vault.Id).Distinct().ToList();
    }

    private async Task BackUpAsync(CancellationToken ct)
    {
        var identity = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(options.Vault + "\n" + bot.VaultPath)))[..16];
        var destination = Path.Combine(options.StateDirectory, $"initial-vault-backup-{identity}.zip");
        if (File.Exists(destination)) return;
        var temporary = destination + ".tmp";
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
            using (var archive = new ZipArchive(stream, ZipArchiveMode.Create))
            {
                await AddDirectoryAsync(archive, bot.VaultPath, ct);
            }
            File.Move(temporary, destination);
        }
        finally { File.Delete(temporary); }
    }

    private async Task AddDirectoryAsync(ZipArchive archive, string directory, CancellationToken ct)
    {
        foreach (var path in Directory.EnumerateFileSystemEntries(directory))
        {
            ct.ThrowIfCancellationRequested();
            var attributes = File.GetAttributes(path);
            if (attributes.HasFlag(FileAttributes.ReparsePoint))
                throw new SyncFailure("SYNC_VAULT_SYMLINK", "The existing vault contains symbolic links. Resolve them before managed adoption.", true);
            if (attributes.HasFlag(FileAttributes.Directory))
                await AddDirectoryAsync(archive, path, ct);
            else
            {
                var entry = archive.CreateEntry(Path.GetRelativePath(bot.VaultPath, path).Replace('\\', '/'));
                await using var input = File.OpenRead(path);
                await using var output = entry.Open();
                await input.CopyToAsync(output, ct);
            }
        }
    }
}
