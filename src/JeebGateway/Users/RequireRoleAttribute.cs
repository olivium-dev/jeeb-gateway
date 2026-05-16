using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace JeebGateway.Users;

/// <summary>
/// Role-check action filter (T-backend-041). Sits in front of every
/// protected route so the role gate is enforced uniformly instead of
/// being open-coded inside each controller.
///
/// Semantics:
/// <list type="bullet">
///   <item>No identity (no JWT sub / X-User-Id) → 401.</item>
///   <item>Identity present but none of the required roles → 403.</item>
///   <item>Identity present with at least one required role → proceed.</item>
/// </list>
///
/// Dual-role accounts (BR-1) work transparently: holding any one of the
/// listed roles satisfies the filter, so a user marked as both
/// <see cref="Roles.Client"/> and <see cref="Roles.Jeeber"/> may hit
/// either family of endpoints without re-issuing tokens.
/// </summary>
[AttributeUsage(AttributeTargets.Method | AttributeTargets.Class, AllowMultiple = false)]
public sealed class RequireRoleAttribute : Attribute, IActionFilter
{
    private readonly string[] _anyOf;

    public RequireRoleAttribute(params string[] anyOf)
    {
        if (anyOf is null || anyOf.Length == 0)
        {
            throw new ArgumentException("RequireRole requires at least one role.", nameof(anyOf));
        }
        _anyOf = anyOf;
    }

    public void OnActionExecuting(ActionExecutingContext context)
    {
        if (!UserIdentity.TryGetUserId(context.HttpContext, out _, out var unauthorized))
        {
            context.Result = unauthorized;
            return;
        }

        if (!UserIdentity.HasAnyRole(context.HttpContext, _anyOf))
        {
            context.Result = new ObjectResult(new ProblemDetails
            {
                Title = "Forbidden: missing required role.",
                Detail = $"This endpoint requires one of: {string.Join(", ", _anyOf)}.",
                Status = StatusCodes.Status403Forbidden,
                Type = "https://jeeb.dev/errors/forbidden-role"
            })
            {
                StatusCode = StatusCodes.Status403Forbidden
            };
        }
    }

    public void OnActionExecuted(ActionExecutedContext context) { }
}
