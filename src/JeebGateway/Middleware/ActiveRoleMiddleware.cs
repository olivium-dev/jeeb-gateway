using JeebGateway.Users;

namespace JeebGateway.Middleware;

/// <summary>
/// T-backend-041. Reads the user's persisted active role from the store
/// and injects it into <c>HttpContext.Items["ActiveRole"]</c> so
/// downstream controllers and filters can check the effective role
/// without re-querying the store.
///
/// For JWT-authenticated requests the middleware also validates that the
/// "active_role" claim (if present) matches the persisted value — a
/// stale token issued before a role switch is rejected with 403 so the
/// mobile app is forced to re-issue with the updated role context.
///
/// Unauthenticated requests pass through untouched (the role check is
/// meaningless without an identity).
/// </summary>
public class ActiveRoleMiddleware
{
    private readonly RequestDelegate _next;

    public ActiveRoleMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        if (UserIdentity.TryGetUserId(context, out var userId, out _))
        {
            var store = context.RequestServices.GetService<IUsersStore>();
            if (store is not null)
            {
                var profile = await store.GetByIdAsync(userId, context.RequestAborted);
                if (profile is not null)
                {
                    context.Items["ActiveRole"] = profile.ActiveRole;

                    var claimedActiveRole = context.User?.FindFirst("active_role")?.Value;
                    if (!string.IsNullOrEmpty(claimedActiveRole)
                        && !string.Equals(claimedActiveRole, profile.ActiveRole, StringComparison.OrdinalIgnoreCase))
                    {
                        context.Response.StatusCode = StatusCodes.Status403Forbidden;
                        await context.Response.WriteAsJsonAsync(new
                        {
                            type = "https://jeeb.dev/errors/stale-active-role",
                            title = "Active role in token does not match persisted role.",
                            detail = $"Token claims active_role='{claimedActiveRole}', but current active role is '{profile.ActiveRole}'. Re-authenticate to get an updated token.",
                            status = 403
                        }, context.RequestAborted);
                        return;
                    }
                }
            }
        }

        await _next(context);
    }
}
