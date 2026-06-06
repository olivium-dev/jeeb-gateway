using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Policy;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace JeebGateway.Auth.Capabilities;

/// <summary>
/// ADR-005 Layer 2 — shapes the framework's 403 (capability denied) into the gateway's RFC7807
/// <c>application/problem+json</c> body, matching the convention the rest of the gateway uses
/// (<c>https://jeeb.dev/errors/...</c>, see <c>Security/*Middleware.cs</c> and the legacy
/// <c>RequireRoleAttribute</c>).
///
/// <para><b>403 only.</b> This handler intercepts ONLY the Forbidden outcome. The 401
/// (Challenge / unauthenticated — Layer 1) path is DELEGATED untouched to the default handler so
/// the Layer-1 response shape (and the JwtBearer challenge headers) are preserved exactly. This
/// keeps the 401-vs-403 separation that is the load-bearing tell of the two-layer design: Layer 1
/// owns 401, Layer 2 owns 403.</para>
///
/// <para>Registered as a singleton in Program.cs; ASP.NET Core uses the last-registered
/// <see cref="IAuthorizationMiddlewareResultHandler"/>.</para>
/// </summary>
public sealed class CapabilityForbiddenResultHandler : IAuthorizationMiddlewareResultHandler
{
    private static readonly AuthorizationMiddlewareResultHandler Default = new();

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task HandleAsync(
        RequestDelegate next,
        HttpContext context,
        AuthorizationPolicy policy,
        PolicyAuthorizationResult authorizeResult)
    {
        // Only shape the FORBIDDEN outcome (authenticated caller, wrong user type → Layer 2 403).
        // Challenged (unauthenticated → Layer 1 401) and Succeeded are left to the default handler
        // so the Layer-1 shape and challenge semantics are untouched.
        if (authorizeResult.Forbidden && !context.Response.HasStarted)
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            context.Response.ContentType = "application/problem+json";

            var problem = new ProblemDetails
            {
                Title = "Forbidden: missing required capability.",
                Detail = "Your user type does not hold the capability required for this action.",
                Status = StatusCodes.Status403Forbidden,
                Type = "https://jeeb.dev/errors/forbidden-capability",
                Instance = context.Request.Path
            };
            problem.Extensions["traceId"] =
                System.Diagnostics.Activity.Current?.Id ?? context.TraceIdentifier;

            await context.Response.WriteAsync(JsonSerializer.Serialize(problem, JsonOptions));
            return;
        }

        await Default.HandleAsync(next, context, policy, authorizeResult);
    }
}
