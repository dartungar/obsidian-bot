using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Policy;
using Microsoft.OpenApi;
using ObsidianBot.Api;
using ObsidianBot.Configuration;
using ObsidianBot.Services;
using Telegram.Bot;

if (args is ["--health-check", var healthUrl])
{
    using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
    try { Environment.ExitCode = (await http.GetAsync(healthUrl)).IsSuccessStatusCode ? 0 : 1; }
    catch { Environment.ExitCode = 1; }
    return;
}

var builder = WebApplication.CreateBuilder(args);

var botOptions = ObsidianBotOptions.Load(builder.Configuration);
builder.Services.AddSingleton(botOptions);
var syncOptions = ObsidianSyncOptions.Load(builder.Configuration, botOptions);
builder.Services.AddSingleton(syncOptions);
builder.Services.AddSingleton<VaultSyncStateStore>();
if (syncOptions.Enabled && (botOptions.ComponentRole is "sync" or "combined"))
{
    builder.Services.AddSingleton<SyncProcessClient>();
    builder.Services.AddSingleton<VaultBootstrapService>();
    builder.Services.AddHostedService<VaultSyncService>();
}
if (botOptions.RunsTelegram)
{
    builder.Services.AddSingleton<ITelegramBotClient>(serviceProvider =>
    {
        var options = serviceProvider.GetRequiredService<ObsidianBotOptions>();
        return new TelegramBotClient(options.TelegramBotToken);
    });
}

builder.Services.AddSingleton(new HttpClient { Timeout = TimeSpan.FromSeconds(60) });
builder.Services.AddSingleton<OpenAiEmbeddingClient>();
builder.Services.AddSingleton<VaultAccessPolicy>();
builder.Services.AddSingleton<VaultSearchService>();
builder.Services.AddSingleton<VaultNotesService>();
builder.Services.AddSingleton<ProposalStore>();
builder.Services.AddSingleton<ChangeProposalService>();
builder.Services.AddSingleton<DirectChangeStore>();
builder.Services.AddSingleton<DirectChangeSnapshotStore>();
builder.Services.AddSingleton<DirectChangeService>();
if (botOptions.RunsTelegram)
{
    builder.Services.AddSingleton<ObsidianVaultWriter>();
    builder.Services.AddHostedService<ObsidianBotService>();
    if (syncOptions.Enabled) builder.Services.AddHostedService<SyncHealthNotificationService>();
}

if (botOptions.RunsSearchIndexer)
{
    builder.Services.AddHostedService<VaultSearchIndexer>();
}

if (botOptions.RunsPublisher)
{
    builder.Services.AddHostedService<ProposalPublisher>();
}

builder.Services
    .AddAuthentication()
    .AddScheme<AuthenticationSchemeOptions, ApiTokenAuthenticationHandler>(
        ApiTokenAuthenticationHandler.SchemeName,
        _ => { });
builder.Services.AddAuthorization(ApiAuthorization.Configure);
builder.Services.AddSingleton<IAuthorizationMiddlewareResultHandler, ApiAuthorizationResultHandler>();
builder.Services.AddOpenApi("v1", options =>
{
    options.ShouldInclude = description => description.GroupName == "v1";
    options.AddDocumentTransformer((document, _, _) =>
    {
        document.Info = new()
        {
            Title = "Obsidian Agent Capture API",
            Version = "v0.2",
            Description = "Controlled vault API for agents. Routine create and append operations are revision-checked, " +
                          "atomic, audited, and reversible; the v0.1 proposal/review workflow remains available for " +
                          "review-required work. Use OBSIDIAN_AGENT_API_TOKEN for scoped agent operations."
        };
        document.Components ??= new OpenApiComponents();
        document.Components.SecuritySchemes = new Dictionary<string, IOpenApiSecurityScheme>
        {
            ["bearerAuth"] = new OpenApiSecurityScheme
            {
                Type = SecuritySchemeType.Http,
                Scheme = "bearer",
                In = ParameterLocation.Header,
                BearerFormat = "API token",
                Description = "Send `Authorization: Bearer <token>`. Agent tokens may read notes, make permitted direct " +
                              "changes, undo their own direct changes, and create/read proposals; reviewer tokens may " +
                              "review proposals and read audit events."
            }
        };

        foreach (var operation in document.Paths.Values.SelectMany(path => path.Operations ?? []))
        {
            operation.Value.Security ??= [];
            operation.Value.Security.Add(new OpenApiSecurityRequirement
            {
                [new OpenApiSecuritySchemeReference("bearerAuth", document)] = []
            });
            operation.Value.Responses ??= new OpenApiResponses();
            operation.Value.Responses.TryAdd("401", new OpenApiResponse
            {
                Description = "Missing or invalid bearer token."
            });
            operation.Value.Responses.TryAdd("403", new OpenApiResponse
            {
                Description = "The bearer token does not have the scope required by this operation."
            });
        }

        return Task.CompletedTask;
    });
});

var app = builder.Build();
VaultWriteLock.Configure(syncOptions, app.Services.GetRequiredService<VaultSyncStateStore>());

app.MapGet("/healthz", () => Results.Ok(new { status = "ok" })).AllowAnonymous();
app.MapGet("/readyz", (VaultSyncStateStore store) => store.IsReady
    ? Results.Ok(new { status = "ready" })
    : Results.Json(new { status = "initializing" }, statusCode: StatusCodes.Status503ServiceUnavailable)).AllowAnonymous();
app.MapGet("/sync-healthz", (VaultSyncStateStore store) =>
{
    var status = store.Read();
    var healthy = !syncOptions.Enabled || status.Initialized && status.ConsecutiveFailures == 0 &&
        (status.State is "healthy" or "syncing") && status.LastSuccessfulSyncAt is { } at &&
        DateTimeOffset.UtcNow - at <= TimeSpan.FromSeconds(syncOptions.StaleAfterSeconds);
    return Results.Json(new { status = healthy ? "healthy" : "unhealthy" }, statusCode: healthy ? 200 : 503);
}).AllowAnonymous();

if (botOptions.RunsAgentApi)
{
    if (string.IsNullOrWhiteSpace(botOptions.AgentApiToken))
    {
        app.Logger.LogWarning("OBSIDIAN_AGENT_API_TOKEN is not configured; the agent API will reject requests.");
    }

    if (string.IsNullOrWhiteSpace(botOptions.ReviewApiToken))
    {
        app.Logger.LogWarning("OBSIDIAN_REVIEW_API_TOKEN is not configured; proposal reviews will be unavailable.");
    }

    app.MapOpenApi("/openapi/{documentName}.json").AllowAnonymous();

    var agentApi = app.MapGroup("/v1")
        .WithGroupName("v1")
        .WithTags("Agent capture")
        .RequireAuthorization(new AuthorizeAttribute
    {
        AuthenticationSchemes = ApiTokenAuthenticationHandler.SchemeName
    });
    agentApi.AddEndpointFilter(async (context, next) =>
    {
        var path = context.HttpContext.Request.Path.Value?.TrimEnd('/');
        if (path is not ("/v1/sync/status" or "/v1/capabilities" or "/v1") &&
            !context.HttpContext.RequestServices.GetRequiredService<VaultSyncStateStore>().IsReady)
            return Results.Json(new ObsidianBot.Models.ApiErrorResponse("VAULT_INITIALIZING",
                "The vault is waiting for its initial sync."), statusCode: StatusCodes.Status503ServiceUnavailable);
        return await next(context);
    });
    AgentApiEndpoints.Map(agentApi);
}

await app.RunAsync();
