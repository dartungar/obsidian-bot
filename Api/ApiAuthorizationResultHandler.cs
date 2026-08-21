using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Policy;
using ObsidianBot.Models;

namespace ObsidianBot.Api;

public sealed class ApiAuthorizationResultHandler : IAuthorizationMiddlewareResultHandler
{
    private readonly AuthorizationMiddlewareResultHandler _fallback = new();

    public Task HandleAsync(
        RequestDelegate next,
        HttpContext context,
        AuthorizationPolicy policy,
        PolicyAuthorizationResult authorizeResult)
    {
        if (authorizeResult.Forbidden && context.Request.Path.StartsWithSegments("/v1"))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return context.Response.WriteAsJsonAsync(new ApiErrorResponse(
                "OPERATION_NOT_ALLOWED",
                "The bearer token does not have the scope required by this operation."));
        }

        return _fallback.HandleAsync(next, context, policy, authorizeResult);
    }
}
