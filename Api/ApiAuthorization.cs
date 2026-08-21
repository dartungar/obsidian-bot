using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;

namespace ObsidianBot.Api;

public static class ApiAuthorization
{
    public const string AgentReadPolicy = "agent-read";
    public const string ProposalCreatePolicy = "proposal-create";
    public const string ProposalReadPolicy = "proposal-read";
    public const string ProposalReviewPolicy = "proposal-review";
    public const string AuditReadPolicy = "audit-read";

    public static void Configure(AuthorizationOptions options)
    {
        AddScopePolicy(options, AgentReadPolicy, "notes:read");
        AddScopePolicy(options, ProposalCreatePolicy, "proposals:create");
        AddScopePolicy(options, ProposalReadPolicy, "proposals:read");
        AddScopePolicy(options, ProposalReviewPolicy, "proposals:review");
        AddScopePolicy(options, AuditReadPolicy, "audit:read");
    }

    private static void AddScopePolicy(AuthorizationOptions options, string policyName, string scope)
    {
        options.AddPolicy(policyName, policy => policy.RequireAssertion(context =>
            context.User.HasClaim("scope", scope)));
    }
}
