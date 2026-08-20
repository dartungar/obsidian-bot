using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using ObsidianBot.Api;
using ObsidianBot.Configuration;
using ObsidianBot.Services;
using Telegram.Bot;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSingleton(ObsidianBotOptions.Load(builder.Configuration));
builder.Services.AddSingleton<ITelegramBotClient>(serviceProvider =>
{
    var options = serviceProvider.GetRequiredService<ObsidianBotOptions>();
    return new TelegramBotClient(options.TelegramBotToken);
});
builder.Services.AddSingleton(new HttpClient { Timeout = TimeSpan.FromSeconds(60) });
builder.Services.AddSingleton<OpenAiEmbeddingClient>();
builder.Services.AddSingleton<ObsidianVaultWriter>();
builder.Services.AddSingleton<VaultSearchService>();
builder.Services.AddHostedService<VaultSearchIndexer>();
builder.Services.AddHostedService<ObsidianBotService>();
builder.Services
    .AddAuthentication()
    .AddScheme<AuthenticationSchemeOptions, ApiTokenAuthenticationHandler>(
        ApiTokenAuthenticationHandler.SchemeName,
        _ => { });
builder.Services.AddAuthorization();

var app = builder.Build();

app.MapGet("/healthz", () => Results.Ok(new { status = "ok" })).AllowAnonymous();

if (string.IsNullOrWhiteSpace(app.Services.GetRequiredService<ObsidianBotOptions>().ApiToken))
{
    app.Logger.LogWarning("OBSIDIAN_API_TOKEN is not configured; all API requests will be rejected.");
}

var api = app.MapGroup("/api").RequireAuthorization(new AuthorizeAttribute
{
    AuthenticationSchemes = ApiTokenAuthenticationHandler.SchemeName
});
api.MapGet("/", () => Results.Ok(new
{
    commands = new[] { "add", "search", "semantic", "cancel" }
}));
api.MapPost("/commands/cancel", () => Results.Ok(new ApiCancelResponse("cancelled")));
api.MapPost("/commands/{command}", ApiCommandEndpoints.ExecuteAsync);

await app.RunAsync();
