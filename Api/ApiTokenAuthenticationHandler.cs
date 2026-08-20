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

        if (string.IsNullOrWhiteSpace(_botOptions.ApiToken))
        {
            return Task.FromResult(AuthenticateResult.Fail("OBSIDIAN_API_TOKEN is not configured."));
        }

        if (!AuthenticationHeaderValue.TryParse(authorization.ToString(), out var header) ||
            !string.Equals(header.Scheme, "Bearer", StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrEmpty(header.Parameter))
        {
            return Task.FromResult(AuthenticateResult.Fail("A bearer token is required."));
        }

        var supplied = Encoding.UTF8.GetBytes(header.Parameter);
        var expected = Encoding.UTF8.GetBytes(_botOptions.ApiToken);
        if (!CryptographicOperations.FixedTimeEquals(supplied, expected))
        {
            return Task.FromResult(AuthenticateResult.Fail("Invalid bearer token."));
        }

        var identity = new ClaimsIdentity(
            [new Claim(ClaimTypes.Name, "obsidian-api")],
            SchemeName);
        var principal = new ClaimsPrincipal(identity);
        var ticket = new AuthenticationTicket(principal, SchemeName);
        return Task.FromResult(AuthenticateResult.Success(ticket));
    }

    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        Response.StatusCode = StatusCodes.Status401Unauthorized;
        Response.Headers["WWW-Authenticate"] = "Bearer";
        return Task.CompletedTask;
    }
}
