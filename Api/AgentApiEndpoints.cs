using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using System.Text.Json;
using ObsidianBot.Models;
using ObsidianBot.Services;

namespace ObsidianBot.Api;

public static class AgentApiEndpoints
{
    public static void Map(RouteGroupBuilder api)
    {
        api.MapGet("/", () => Results.Ok(new AgentApiDiscoveryResponse(
            "v0.2",
            "/openapi/v1.json",
            ["capabilities", "notes", "note-changes", "changes", "change-proposals", "audit-events"])))
            .WithSummary("Discover agent API resources")
            .WithDescription("Use the public OpenAPI document at `/openapi/v1.json` to discover the full contract.")
            .Produces<AgentApiDiscoveryResponse>(StatusCodes.Status200OK)
            .RequireAuthorization(ApiAuthorization.AgentReadPolicy);

        api.MapGet("/capabilities", (DirectChangeService changes) => Results.Ok(changes.GetCapabilities()))
            .WithSummary("Get current direct-edit capabilities")
            .WithDescription("Returns the server-enforced direct-edit policy. Clients cannot override this policy in a mutation request.")
            .Produces<AgentCapabilitiesResponse>(StatusCodes.Status200OK)
            .RequireAuthorization(ApiAuthorization.AgentReadPolicy);

        api.MapGet("/notes", SearchNotesAsync)
            .WithSummary("Search readable notes")
            .WithDescription("Search full-text, semantic, or hybrid indexes within the agent-readable vault folders.")
            .Produces<AgentSearchResponse>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status400BadRequest)
            .RequireAuthorization(ApiAuthorization.AgentReadPolicy);
        api.MapGet("/notes/{noteId}", GetNoteAsync)
            .WithSummary("Read a note by opaque ID")
            .Produces<AgentNoteResponse>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status404NotFound)
            .RequireAuthorization(ApiAuthorization.AgentReadPolicy);
        api.MapGet("/notes/{noteId}/sections", GetSectionsAsync)
            .WithSummary("List a note's sections")
            .Produces<AgentSectionListResponse>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status404NotFound)
            .RequireAuthorization(ApiAuthorization.AgentReadPolicy);
        api.MapGet("/notes/{noteId}/sections/{sectionId}", GetSectionAsync)
            .WithSummary("Read a section by opaque ID")
            .Produces<AgentSectionResponse>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status404NotFound)
            .RequireAuthorization(ApiAuthorization.AgentReadPolicy);
        api.MapGet("/notes/{noteId}/links", GetLinksAsync)
            .WithSummary("List a note's links")
            .Produces<AgentLinksResponse>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status404NotFound)
            .RequireAuthorization(ApiAuthorization.AgentReadPolicy);

        api.MapPost("/note-changes", ApplyDirectChangeAsync)
            .WithSummary("Apply one permitted direct note change")
            .WithDescription("Requires Idempotency-Key. Applies only a policy-permitted create or additive section/task append; dryRun validates without writing.")
            .Produces<DirectChangeResponse>(StatusCodes.Status201Created)
            .Produces<DirectChangeResponse>(StatusCodes.Status200OK)
            .Produces<ApiErrorResponse>(StatusCodes.Status400BadRequest)
            .Produces<ApiErrorResponse>(StatusCodes.Status409Conflict)
            .Produces<ApiErrorResponse>(StatusCodes.Status413PayloadTooLarge)
            .Produces<ApiErrorResponse>(StatusCodes.Status422UnprocessableEntity)
            .RequireAuthorization(ApiAuthorization.DirectChangePolicy);
        api.MapGet("/changes/{changeId}", GetDirectChangeAsync)
            .WithSummary("Read one of the caller's direct changes")
            .Produces<DirectChangeResponse>(StatusCodes.Status200OK)
            .Produces<ApiErrorResponse>(StatusCodes.Status404NotFound)
            .RequireAuthorization(ApiAuthorization.ChangeReadPolicy);
        api.MapPost("/changes/{changeId}/undo", UndoDirectChangeAsync)
            .WithSummary("Undo one of the caller's direct changes")
            .WithDescription("Undo is available only within the configured window while the note is still at the direct change's resulting revision.")
            .Produces<UndoDirectChangeResponse>(StatusCodes.Status201Created)
            .Produces<ApiErrorResponse>(StatusCodes.Status409Conflict)
            .Produces<ApiErrorResponse>(StatusCodes.Status422UnprocessableEntity)
            .RequireAuthorization(ApiAuthorization.ChangeUndoPolicy);

        api.MapPost("/change-proposals", CreateProposalAsync)
            .WithSummary("Create an immutable change proposal")
            .WithDescription("Requires `Idempotency-Key`. The request only creates a preview; it never writes to the vault.")
            .Produces<ProposalResponse>(StatusCodes.Status201Created)
            .Produces<ProposalResponse>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status409Conflict)
            .RequireAuthorization(ApiAuthorization.ProposalCreatePolicy);
        api.MapGet("/change-proposals", ListProposalsAsync)
            .WithSummary("List change proposals")
            .Produces<ProposalListResponse>(StatusCodes.Status200OK)
            .RequireAuthorization(ApiAuthorization.ProposalReadPolicy);
        api.MapGet("/change-proposals/{proposalId}", GetProposalAsync)
            .WithSummary("Read a change proposal")
            .Produces<ProposalResponse>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status404NotFound)
            .RequireAuthorization(ApiAuthorization.ProposalReadPolicy);
        api.MapPost("/change-proposals/{proposalId}/reviews", ReviewProposalAsync)
            .WithSummary("Approve or reject a proposal")
            .WithDescription("Reviewer token only. Approval must bind the returned preview hash exactly.")
            .Produces<ProposalResponse>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status404NotFound)
            .RequireAuthorization(ApiAuthorization.ProposalReviewPolicy);
        api.MapGet("/change-proposals/{proposalId}/publication", GetPublicationAsync)
            .WithSummary("Read proposal publication status")
            .Produces<PublicationResponse>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status404NotFound)
            .RequireAuthorization(ApiAuthorization.ProposalReadPolicy);
        api.MapGet("/audit-events", GetAuditEventsAsync)
            .WithSummary("List proposal audit events")
            .WithDescription("Reviewer token only.")
            .Produces<AuditEventListResponse>(StatusCodes.Status200OK)
            .RequireAuthorization(ApiAuthorization.AuditReadPolicy);
    }

    private static async Task<IResult> SearchNotesAsync(
        string? q,
        string? mode,
        int? limit,
        string? folder,
        string? tag,
        string? type,
        string? status,
        string? include,
        VaultNotesService notes,
        ILogger<Program> logger,
        CancellationToken ct) =>
        await ExecuteAsync(async () => Results.Ok(await notes.SearchAsync(
            q ?? string.Empty,
            mode ?? "hybrid",
            limit ?? 8,
            folder,
            tag,
            type,
            status,
            ParseIncludes(include, "snippet"),
            ct)), logger);

    private static async Task<IResult> GetNoteAsync(
        string noteId,
        string? include,
        VaultNotesService notes,
        ILogger<Program> logger,
        CancellationToken ct) =>
        await ExecuteAsync(async () =>
        {
            var note = await notes.FindReadableNoteAsync(noteId, ct);
            return note is null
                ? Results.NotFound(new { error = "Note not found." })
                : Results.Ok(notes.ToNoteResponse(note, ParseIncludes(include, "headings")));
        }, logger);

    private static async Task<IResult> GetSectionsAsync(
        string noteId,
        VaultNotesService notes,
        ILogger<Program> logger,
        CancellationToken ct) =>
        await ExecuteAsync(async () =>
        {
            var note = await notes.FindReadableNoteAsync(noteId, ct);
            return note is null
                ? Results.NotFound(new { error = "Note not found." })
                : Results.Ok(new AgentSectionListResponse(note.Sections.Select(section => new SectionSummary(
                    section.Id, section.HeadingPath, section.Level)).ToArray()));
        }, logger);

    private static async Task<IResult> GetSectionAsync(
        string noteId,
        string sectionId,
        int? contextLines,
        VaultNotesService notes,
        ILogger<Program> logger,
        CancellationToken ct) =>
        await ExecuteAsync(async () =>
        {
            var note = await notes.FindReadableNoteAsync(noteId, ct);
            if (note is null)
            {
                return Results.NotFound(new { error = "Note not found." });
            }

            var section = notes.GetSectionResponse(note, sectionId, contextLines ?? 5);
            return section is null
                ? Results.NotFound(new { error = "Section not found." })
                : Results.Ok(section);
        }, logger);

    private static async Task<IResult> GetLinksAsync(
        string noteId,
        string? direction,
        int? limit,
        VaultNotesService notes,
        ILogger<Program> logger,
        CancellationToken ct) =>
        await ExecuteAsync(async () =>
        {
            var note = await notes.FindReadableNoteAsync(noteId, ct);
            return note is null
                ? Results.NotFound(new { error = "Note not found." })
                : Results.Ok(await notes.GetLinksAsync(note, direction ?? "both", limit ?? 30, ct));
        }, logger);

    private static async Task<IResult> CreateProposalAsync(
        CreateChangeProposalRequest request,
        [FromHeader(Name = "Idempotency-Key")] string idempotencyKey,
        ChangeProposalService proposals,
        ILogger<Program> logger,
        CancellationToken ct) =>
        await ExecuteAsync(async () =>
        {
            var created = await proposals.CreateAsync(request, idempotencyKey, ct);
            var response = proposals.ToResponse(created.Proposal);
            return created.IsIdempotentReplay
                ? Results.Ok(response)
                : Results.Created($"/v1/change-proposals/{response.Id}", response);
        }, logger);

    private static async Task<IResult> ApplyDirectChangeAsync(
        DirectNoteChangeRequest request,
        [FromHeader(Name = "Idempotency-Key")] string idempotencyKey,
        HttpContext httpContext,
        DirectChangeService changes,
        ILogger<Program> logger,
        CancellationToken ct) =>
        await ExecuteAsync(async () =>
        {
            var requiredScope = ApiAuthorization.GetRequiredDirectScope(request.Operation);
            if (requiredScope is not null && !httpContext.User.HasClaim("scope", requiredScope))
            {
                return Results.Json(new ApiErrorResponse(
                    "OPERATION_NOT_ALLOWED",
                    "The credential does not allow the requested direct operation.",
                    RequestedOperation: request.Operation),
                    statusCode: StatusCodes.Status403Forbidden);
            }

            var actor = httpContext.User.Identity?.Name ?? "agent";
            var result = await changes.ApplyAsync(request, actor, idempotencyKey, ct);
            return result.Response.Status == "validated" || result.IsIdempotentReplay
                ? Results.Ok(result.Response)
                : Results.Created($"/v1/changes/{result.Response.ChangeId}", result.Response);
        }, logger);

    private static async Task<IResult> GetDirectChangeAsync(
        string changeId,
        HttpContext httpContext,
        DirectChangeService changes,
        ILogger<Program> logger,
        CancellationToken ct) =>
        await ExecuteAsync(async () =>
        {
            var actor = httpContext.User.Identity?.Name ?? "agent";
            var response = await changes.GetAsync(changeId, actor, ct);
            return response is null
                ? Results.Json(new ApiErrorResponse("CHANGE_NOT_FOUND", "The direct change was not found."),
                    statusCode: StatusCodes.Status404NotFound)
                : Results.Ok(response);
        }, logger);

    private static async Task<IResult> UndoDirectChangeAsync(
        string changeId,
        HttpContext httpContext,
        DirectChangeService changes,
        ILogger<Program> logger,
        CancellationToken ct) =>
        await ExecuteAsync(async () =>
        {
            var actor = httpContext.User.Identity?.Name ?? "agent";
            var response = await changes.UndoAsync(changeId, actor, ct);
            return Results.Created($"/v1/changes/{response.ChangeId}", response);
        }, logger);

    private static async Task<IResult> ListProposalsAsync(
        string? state,
        ChangeProposalService proposals,
        ILogger<Program> logger,
        CancellationToken ct) =>
        await ExecuteAsync(async () => Results.Ok(await proposals.ListAsync(state, ct)), logger);

    private static async Task<IResult> GetProposalAsync(
        string proposalId,
        ChangeProposalService proposals,
        ILogger<Program> logger,
        CancellationToken ct) =>
        await ExecuteAsync(async () =>
        {
            var proposal = await proposals.GetAsync(proposalId, ct);
            return proposal is null
                ? Results.NotFound(new { error = "Proposal not found." })
                : Results.Ok(proposal);
        }, logger);

    private static async Task<IResult> ReviewProposalAsync(
        string proposalId,
        ReviewProposalRequest request,
        ChangeProposalService proposals,
        ILogger<Program> logger,
        CancellationToken ct) =>
        await ExecuteAsync(async () =>
        {
            var result = await proposals.ReviewAsync(proposalId, request, ct);
            if (result.Proposal is null)
            {
                return Results.NotFound(new { error = "Proposal not found." });
            }

            return result.Error is null
                ? Results.Ok(proposals.ToResponse(result.Proposal))
                : Results.BadRequest(new { error = result.Error });
        }, logger);

    private static async Task<IResult> GetPublicationAsync(
        string proposalId,
        ChangeProposalService proposals,
        ILogger<Program> logger,
        CancellationToken ct) =>
        await ExecuteAsync(async () =>
        {
            var publication = await proposals.GetPublicationAsync(proposalId, ct);
            return publication is null
                ? Results.NotFound(new { error = "Proposal not found." })
                : Results.Ok(publication);
        }, logger);

    private static async Task<IResult> GetAuditEventsAsync(
        string? proposalId,
        ChangeProposalService proposals,
        ILogger<Program> logger,
        CancellationToken ct) =>
        await ExecuteAsync(async () => Results.Ok(await proposals.ListAuditEventsAsync(proposalId, ct)), logger);

    private static IReadOnlySet<string> ParseIncludes(string? value, string fallback)
    {
        return (string.IsNullOrWhiteSpace(value) ? fallback : value)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(item => item.ToLowerInvariant())
            .ToHashSet(StringComparer.Ordinal);
    }

    private static async Task<IResult> ExecuteAsync(Func<Task<IResult>> operation, ILogger logger)
    {
        try
        {
            return await operation();
        }
        catch (DirectChangeException ex)
        {
            return Results.Json(ex.Error, statusCode: ex.StatusCode);
        }
        catch (ProposalConflictException ex)
        {
            return Results.Conflict(new { error = ex.Message, resolution = "create_a_new_proposal" });
        }
        catch (VaultApiException ex)
        {
            return Results.BadRequest(new { error = ex.Message });
        }
        catch (JsonException)
        {
            return Results.BadRequest(new { error = "The proposal contains invalid JSON data." });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Agent API request failed");
            return Results.Problem(statusCode: StatusCodes.Status500InternalServerError, title: "Agent API request failed.");
        }
    }
}
