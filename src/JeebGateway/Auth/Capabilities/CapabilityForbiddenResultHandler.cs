using System.Text.Json;
using JeebGateway.Security;
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
        // Decide 401 (Layer 1 — no valid caller) vs 403 (Layer 2 — valid caller, wrong user type).
        //
        // The framework reports Forbidden only when the principal is an AUTHENTICATED user. But the
        // gateway's by-design edge path identifies a caller via the trusted X-User-Id header WITHOUT
        // an authenticated principal (mirrored from GatewayAudienceHandler). For such an edge caller a
        // capability denial surfaces as Challenged (not Forbidden), which the default handler would
        // turn into 401 — masking a genuine Layer-2 user-type denial as a Layer-1 auth failure and
        // regressing the legacy RequireRoleAttribute behaviour (it returned 403 for an identified-
        // but-wrong-role caller). So: if authorization was DENIED (Forbidden OR Challenged) AND the
        // caller is an IDENTIFIED caller (authenticated principal OR trusted X-User-Id header), it is
        // a Layer-2 403. A caller that is neither authenticated nor edge-identified is a genuine
        // Layer-1 401 and is delegated untouched to the default handler (preserving JwtBearer
        // challenge headers and the Layer-1 response shape — the 401-vs-403 tell is upheld).
        var denied = authorizeResult.Forbidden || authorizeResult.Challenged;
        if (denied && IsIdentifiedCaller(context) && !context.Response.HasStarted)
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

    /// <summary>
    /// True when the request carries an IDENTIFIED caller by either of the two signals the gateway
    /// already trusts (mirrors <see cref="JeebGateway.Auth.GatewayAudienceHandler"/> /
    /// <see cref="JeebGateway.Users.UserIdentity.TryGetUserId"/>): a validated authenticated
    /// principal, OR the trusted edge <c>X-User-Id</c> header. Used to map an authorization DENIAL
    /// to 403 (Layer 2, wrong user type) rather than 401 (Layer 1, no valid caller).
    /// </summary>
    private static bool IsIdentifiedCaller(HttpContext context)
    {
        if (context.User?.Identity?.IsAuthenticated == true)
        {
            return true;
        }

        // SEC-C1: only treat an X-User-Id header as an identified caller when it comes from a
        // trusted edge (or Development/Testing). Otherwise a forged-header denial would be mapped
        // to a Layer-2 403, implying the spoofed identity was accepted; it must stay a Layer-1 401.
        return EdgeIdentityTrust.HeadersTrusted(context)
            && context.Request.Headers.TryGetValue("X-User-Id", out var header)
            && !string.IsNullOrWhiteSpace(header);
    }
}
