using System.Net.Http.Headers;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;
using ObsidianBot.Configuration;

namespace ObsidianBot.Api;

public sealed class ApiTokenAuthenticationHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    public const string SchemeName = "ApiBearer";

    private readonly ObsidianBotOptions _botOptions;

    public ApiTokenAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        System.Text.Encodings.Web.UrlEncoder encoder,
        ObsidianBotOptions botOptions)
        : base(options, logger, encoder)
    {
        _botOptions = botOptions;
    }

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue("Authorization", out var authorization))
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        if (!AuthenticationHeaderValue.TryParse(authorization.ToString(), out var header) ||
            !string.Equals(header.Scheme, "Bearer", StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrEmpty(header.Parameter))
        {
            return Task.FromResult(AuthenticateResult.Fail("A bearer token is required."));
        }

        var credential = FindCredential(header.Parameter);
        if (credential is null)
        {
            return Task.FromResult(AuthenticateResult.Fail("Invalid bearer token."));
        }

        var identity = new ClaimsIdentity(
            [
                new Claim(ClaimTypes.Name, credential.Name),
                new Claim("actor_type", credential.ActorType),
                .. credential.Scopes.Select(scope => new Claim("scope", scope))
            ],
            SchemeName);
        var principal = new ClaimsPrincipal(identity);
        var ticket = new AuthenticationTicket(principal, SchemeName);
        return Task.FromResult(AuthenticateResult.Success(ticket));
    }

    private ApiCredential? FindCredential(string suppliedToken)
    {
        var credentials = new[]
        {
            new ApiCredential(
                _botOptions.AgentApiToken,
                "agent",
                "agent",
                [
                    "notes:read",
                    "notes:create",
                    "notes:append-section",
                    "notes:append-task",
                    "changes:read",
                    "changes:undo-own",
                    "proposals:create",
                    "proposals:read"
                ]),
            new ApiCredential(
                _botOptions.ReviewApiToken,
                "reviewer",
                "human",
                ["proposals:read", "proposals:review", "audit:read"])
        };

        var supplied = Encoding.UTF8.GetBytes(suppliedToken);
        foreach (var credential in credentials)
        {
            if (string.IsNullOrWhiteSpace(credential.Token))
            {
                continue;
            }

            var expected = Encoding.UTF8.GetBytes(credential.Token);
            if (CryptographicOperations.FixedTimeEquals(supplied, expected))
            {
                return credential;
            }
        }

        return null;
    }

    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        Response.StatusCode = StatusCodes.Status401Unauthorized;
        Response.Headers["WWW-Authenticate"] = "Bearer";
        return Response.WriteAsJsonAsync(new ObsidianBot.Models.ApiErrorResponse(
            "AUTHENTICATION_REQUIRED",
            "A valid bearer token is required."));
    }

    private sealed record ApiCredential(string Token, string Name, string ActorType, IReadOnlyList<string> Scopes);
}
