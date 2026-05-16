using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace JeebGateway.Users;

/// <summary>
/// Action attribute applied to Client/Jeeber mutations that may not be
/// performed by a suspended user (T-backend-030). Returns 403 with the
/// suspension reason in <c>ProblemDetails.Detail</c> so the mobile app
/// can render the reason banner without a second lookup.
///
/// Admin endpoints intentionally do NOT carry this attribute — an
/// operator must still be able to lift the suspension. Unauthenticated
/// callers fall through to the controller's own 401 path.
/// </summary>
[AttributeUsage(AttributeTargets.Method | AttributeTargets.Class, AllowMultiple = false)]
public sealed class RequireActiveUserAttribute : Attribute, IAsyncActionFilter
{
    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        if (!UserIdentity.TryGetUserId(context.HttpContext, out var userId, out _))
        {
            // No identity → let the action's own 401 handling run.
            await next();
            return;
        }

        var store = context.HttpContext.RequestServices.GetService(typeof(IUsersStore)) as IUsersStore;
        if (store is null)
        {
            await next();
            return;
        }

        var profile = await store.GetByIdAsync(userId, context.HttpContext.RequestAborted);
        if (profile is { IsSuspended: true })
        {
            context.Result = new ObjectResult(new ProblemDetails
            {
                Title = "Account is suspended.",
                Detail = profile.SuspensionReason ?? "Contact support.",
                Status = StatusCodes.Status403Forbidden,
                Type = "https://jeeb.dev/errors/account-suspended"
            })
            {
                StatusCode = StatusCodes.Status403Forbidden
            };
            return;
        }

        await next();
    }
}
