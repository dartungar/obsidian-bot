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
    public const string DirectChangePolicy = "direct-change";
    public const string ChangeReadPolicy = "change-read";
    public const string ChangeUndoPolicy = "change-undo";

    public static void Configure(AuthorizationOptions options)
    {
        AddScopePolicy(options, AgentReadPolicy, "notes:read");
        AddScopePolicy(options, ProposalCreatePolicy, "proposals:create");
        AddScopePolicy(options, ProposalReadPolicy, "proposals:read");
        AddScopePolicy(options, ProposalReviewPolicy, "proposals:review");
        AddScopePolicy(options, AuditReadPolicy, "audit:read");
        AddAnyScopePolicy(options, DirectChangePolicy, "notes:create", "notes:append-section", "notes:append-task");
        AddScopePolicy(options, ChangeReadPolicy, "changes:read");
        AddScopePolicy(options, ChangeUndoPolicy, "changes:undo-own");
    }

    private static void AddScopePolicy(AuthorizationOptions options, string policyName, string scope)
    {
        options.AddPolicy(policyName, policy => policy.RequireAssertion(context =>
            context.User.HasClaim("scope", scope)));
    }

    private static void AddAnyScopePolicy(AuthorizationOptions options, string policyName, params string[] scopes)
    {
        options.AddPolicy(policyName, policy => policy.RequireAssertion(context =>
            scopes.Any(scope => context.User.HasClaim("scope", scope))));
    }

    public static string? GetRequiredDirectScope(string? operation) => operation?.Trim().ToLowerInvariant() switch
    {
        "create_note" => "notes:create",
        "append_section" => "notes:append-section",
        "append_task" => "notes:append-task",
        _ => null
    };
}
