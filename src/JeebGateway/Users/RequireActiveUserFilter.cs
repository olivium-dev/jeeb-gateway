using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using JeebGateway.Services.Clients;

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

        var owner = context.HttpContext.RequestServices
            .GetService(typeof(IBanServiceClient)) as IBanServiceClient;
        if (owner is null)
        {
            context.Result = Unavailable();
            return;
        }

        BanStatusesResult statuses;
        try
        {
            statuses = await owner.GetStatusAsync(
                userId, context.HttpContext.RequestAborted);
        }
        catch (OperationCanceledException) when (context.HttpContext.RequestAborted.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            // A ban-owner outage must never be interpreted as "active". That would
            // silently bypass an administrator suspension after a gateway restart.
            context.Result = Unavailable();
            return;
        }

        var active = statuses.BanStatuses
            .Where(status => status.IsCurrentlyBanned)
            .OrderByDescending(status => status.LastUpdated)
            .FirstOrDefault();
        if (active is not null)
        {
            // D16: ban-service ships i18n TEMPLATES as its message; never render one as prose.
            var problem = new ProblemDetails
            {
                Title = "Account is suspended.",
                Detail = Moderation.ModerationReason.Humanize(active.Message),
                Status = StatusCodes.Status403Forbidden,
                Type = "https://jeeb.dev/errors/account-suspended"
            };
            var code = Moderation.ModerationReason.CodeOf(active.Message);
            if (code is not null) problem.Extensions["reasonCode"] = code;

            context.Result = new ObjectResult(problem)
            {
                StatusCode = StatusCodes.Status403Forbidden
            };
            return;
        }

        await next();
    }

    private static ObjectResult Unavailable() => new(new ProblemDetails
    {
        Title = "Account status is unavailable.",
        Detail = "The ban service could not confirm whether this account is active.",
        Status = StatusCodes.Status503ServiceUnavailable,
        Type = "https://jeeb.dev/errors/account-status-unavailable",
    })
    {
        StatusCode = StatusCodes.Status503ServiceUnavailable,
    };
}
