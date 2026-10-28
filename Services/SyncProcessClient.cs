using System.Diagnostics;
using System.Text;
using ObsidianBot.Configuration;

namespace ObsidianBot.Services;

public sealed class SyncProcessClient(ObsidianSyncOptions options)
{
    public async Task<SyncProcessResult> RunAsync(IEnumerable<string> arguments, int timeoutSeconds, CancellationToken ct)
    {
        var start = new ProcessStartInfo(options.Executable)
        {
            UseShellExecute = false, RedirectStandardInput = true,
            RedirectStandardOutput = true, RedirectStandardError = true
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        start.Environment["XDG_CONFIG_HOME"] = options.ConfigDirectory;
        var token = options.ReadAuthToken();
        start.Environment.Remove("OBSIDIAN_AUTH_TOKEN");
        if (!string.IsNullOrEmpty(token)) start.Environment["OBSIDIAN_AUTH_TOKEN"] = token;
        using var process = new Process { StartInfo = start };
        // Never log arguments, environment, or raw CLI output: setup arguments contain secrets.
        process.Start();
        process.StandardInput.Close();
        var output = new StringBuilder();
        var error = new StringBuilder();
        var fullySynced = false;
        var stdout = DrainAsync(process.StandardOutput, output, line => fullySynced |= line.Trim() == "Fully synced");
        var stderr = DrainAsync(process.StandardError, error, _ => { });
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
        try
        {
            await process.WaitForExitAsync(deadline.Token);
            await Task.WhenAll(stdout, stderr).WaitAsync(deadline.Token);
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(CancellationToken.None);
            await Task.WhenAll(stdout, stderr).WaitAsync(TimeSpan.FromSeconds(5));
            ct.ThrowIfCancellationRequested();
            throw new SyncFailure("SYNC_TIMEOUT", "The sync command timed out and was terminated.");
        }
        return new(process.ExitCode, output.ToString(), error.ToString(), fullySynced);
    }

    private static async Task DrainAsync(StreamReader reader, StringBuilder buffer, Action<string> observe)
    {
        while (await reader.ReadLineAsync() is { } line)
        {
            observe(line);
            // Keep bounded diagnostics for classification; even a large sync must drain both pipes.
            if (buffer.Length + line.Length > 65536) buffer.Clear();
            buffer.AppendLine(line.Length > 65536 ? line[..65536] : line);
        }
    }
}

public sealed record SyncProcessResult(int ExitCode, string Output, string Error, bool FullySynced)
{
    public void EnsureSuccess(bool requireCompletion = false)
    {
        if (ExitCode == 0 && (!requireCompletion || FullySynced && string.IsNullOrWhiteSpace(Error))) return;
        var diagnostic = Error + Output;
        if (ExitCode == 3)
            throw new SyncFailure("SYNC_SETUP_REQUIRED", "The headless vault configuration is missing or the remote vault is unavailable.", true);
        if (ExitCode == 2 || diagnostic.Contains("No account logged in", StringComparison.OrdinalIgnoreCase) ||
            diagnostic.Contains("Invalid token", StringComparison.OrdinalIgnoreCase) ||
            diagnostic.Contains("Unauthorized", StringComparison.OrdinalIgnoreCase) ||
            diagnostic.Contains("401", StringComparison.Ordinal))
            throw new SyncFailure("SYNC_CREDENTIALS", "Obsidian authentication or the vault encryption password needs attention.", true);
        throw new SyncFailure(requireCompletion && ExitCode == 0 ? "SYNC_INCOMPLETE" : "SYNC_COMMAND_FAILED",
            "The headless client did not complete successfully. Check credentials, connectivity, storage, and subscription limits.");
    }
}

public sealed class SyncFailure(string code, string safeMessage, bool needsAttention = false) : Exception(safeMessage)
{
    public string Code { get; } = code;
    public bool NeedsAttention { get; } = needsAttention;
}
