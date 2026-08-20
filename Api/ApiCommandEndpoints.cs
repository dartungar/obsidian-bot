using System.Globalization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using ObsidianBot.Models;
using ObsidianBot.Services;

namespace ObsidianBot.Api;

public static class ApiCommandEndpoints
{
    public static async Task<IResult> ExecuteAsync(
        string command,
        ApiCommandRequest request,
        ObsidianVaultWriter vaultWriter,
        VaultSearchService vaultSearch,
        ILoggerFactory loggerFactory,
        CancellationToken ct)
    {
        var normalizedCommand = command.Trim().TrimStart('/').ToLowerInvariant();

        try
        {
            return normalizedCommand switch
            {
                "add" => await AddAsync(request, vaultWriter, ct),
                "search" => await SearchAsync(request, vaultSearch, semantic: false, ct),
                "semantic" => await SearchAsync(request, vaultSearch, semantic: true, ct),
                "cancel" => Results.Ok(new ApiCancelResponse("cancelled")),
                _ => Results.NotFound(new { error = $"Unknown command '{command}'." })
            };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (ApiValidationException ex)
        {
            return BadRequest(ex.Message);
        }
        catch (Exception ex)
        {
            loggerFactory.CreateLogger("ObsidianBot.Api")
                .LogWarning(ex, "API command {Command} failed", normalizedCommand);
            return Results.Problem(statusCode: StatusCodes.Status500InternalServerError, title: "Command failed.");
        }
    }

    private static async Task<IResult> AddAsync(
        ApiCommandRequest request,
        ObsidianVaultWriter vaultWriter,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Content))
        {
            return BadRequest("content is required for the add command.");
        }

        if (string.IsNullOrWhiteSpace(request.Destination))
        {
            return BadRequest("destination is required for the add command.");
        }

        var destination = request.Destination.Trim().ToLowerInvariant();
        SaveResult result;

        if (request.AsTask)
        {
            result = destination switch
            {
                "today" => await vaultWriter.SaveTaskToDailyNoteAsync(
                    vaultWriter.GetLocalDateToday(), request.Content, ct),
                "tomorrow" => await vaultWriter.SaveTaskToDailyNoteAsync(
                    vaultWriter.GetLocalDateToday().AddDays(1), request.Content, ct),
                "inbox" => await vaultWriter.SaveTaskToInboxAsync(request.Content, ct),
                _ => throw new ApiValidationException(
                    "Task destination must be one of: today, tomorrow, inbox.")
            };
        }
        else
        {
            var pending = new PendingCapture { TextContent = request.Content };
            result = destination switch
            {
                "today" => await vaultWriter.SaveToDailyNoteAsync(vaultWriter.GetLocalDateToday(), pending, ct),
                "yesterday" => await vaultWriter.SaveToDailyNoteAsync(
                    vaultWriter.GetLocalDateToday().AddDays(-1), pending, ct),
                "inbox" => await vaultWriter.SaveToInboxAsync(pending, ct),
                "date" => await vaultWriter.SaveToDailyNoteAsync(ParseDate(request.Date), pending, ct),
                _ => throw new ApiValidationException(
                    "Destination must be one of: today, yesterday, inbox, date.")
            };
        }

        return Results.Ok(new ApiSaveResponse("saved", result.Target, result.NotePath, result.MediaPath));
    }

    private static async Task<IResult> SearchAsync(
        ApiCommandRequest request,
        VaultSearchService vaultSearch,
        bool semantic,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Query))
        {
            return BadRequest("query is required for search commands.");
        }

        if (semantic)
        {
            var results = await vaultSearch.SearchSemanticAsync(request.Query.Trim(), ct);
            return Results.Ok(new ApiSemanticSearchResponse(results));
        }

        var combined = await vaultSearch.SearchCombinedAsync(request.Query.Trim(), ct);
        return Results.Ok(new ApiSearchResponse(
            combined.FullText,
            combined.Semantic,
            combined.SemanticSearchConfigured));
    }

    private static DateOnly ParseDate(string? value)
    {
        if (!string.IsNullOrWhiteSpace(value) &&
            (DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var exactDate) ||
             DateOnly.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out exactDate)))
        {
            return exactDate;
        }

        throw new ApiValidationException("date is required and must be a valid date when destination is date.");
    }

    private static IResult BadRequest(string message) => Results.BadRequest(new { error = message });

    private sealed class ApiValidationException : Exception
    {
        public ApiValidationException(string message)
            : base(message)
        {
        }
    }
}

public sealed record ApiCommandRequest(
    string? Content,
    string? Query,
    string? Destination,
    string? Date,
    bool AsTask = false);

public sealed record ApiSaveResponse(string Status, string Target, string NotePath, string? MediaPath);

public sealed record ApiSearchResponse(
    IReadOnlyList<SearchResult> FullText,
    IReadOnlyList<SearchResult> Semantic,
    bool SemanticSearchConfigured);

public sealed record ApiSemanticSearchResponse(IReadOnlyList<SearchResult> Results);

public sealed record ApiCancelResponse(string Status);
