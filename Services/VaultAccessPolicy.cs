using ObsidianBot.Configuration;
using ObsidianBot.Models;

namespace ObsidianBot.Services;

public sealed class VaultAccessPolicy
{
    private readonly ObsidianBotOptions _options;

    public VaultAccessPolicy(ObsidianBotOptions options)
    {
        _options = options;
    }

    public bool CanRead(string relativePath)
    {
        var normalized = NormalizePath(relativePath);
        return !IsDenied(normalized) &&
               (_options.AgentReadableFolders.Count == 0 ||
                _options.AgentReadableFolders.Any(folder => IsWithinFolder(normalized, folder)));
    }

    public bool CanWrite(string relativePath)
    {
        var normalized = NormalizePath(relativePath);
        return !IsDenied(normalized) &&
               _options.AgentWritableFolders.Any(folder => IsWithinFolder(normalized, folder));
    }

    public IReadOnlyList<WritableFolder> GetWritableFolders() => _options.AgentWritableFolders
        .Select(NormalizePath)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .Where(folder => !IsDenied(folder))
        .Select(folder => new WritableFolder("folder_" + ShortHash("folder\n" + folder), folder))
        .ToArray();

    public IReadOnlyList<string> GetProtectedPathPrefixes() => _options.AgentDeniedFolders
        .Select(NormalizePath)
        .Where(folder => !string.IsNullOrWhiteSpace(folder))
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToArray();

    public static bool IsWithinFolder(string path, string folder)
    {
        var normalizedFolder = NormalizePath(folder);
        return string.IsNullOrEmpty(normalizedFolder) ||
               string.Equals(path, normalizedFolder, StringComparison.OrdinalIgnoreCase) ||
               path.StartsWith(normalizedFolder + "/", StringComparison.OrdinalIgnoreCase);
    }

    private bool IsDenied(string relativePath)
    {
        var normalized = NormalizePath(relativePath);
        return _options.AgentDeniedFolders.Any(folder =>
            IsWithinFolder(normalized, folder) ||
            normalized.Split('/', StringSplitOptions.RemoveEmptyEntries)
                .Any(component => string.Equals(component, NormalizePath(folder), StringComparison.OrdinalIgnoreCase)));
    }

    private static string NormalizePath(string path) => path.Replace('\\', '/').Trim().Trim('/');

    private static string ShortHash(string value) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(value))).ToLowerInvariant()[..24];
}
