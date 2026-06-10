using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Options;

namespace JeebGateway.TestControlPlane;

/// <summary>
/// JEB-1502: Action filter that gates <c>/__test/*</c> endpoints behind two
/// checks — in order:
/// <list type="number">
///   <item><see cref="TestControlPlaneOptions.Enabled"/> flag → 404 when off</item>
///   <item><c>X-Test-Control-Plane-Secret</c> header must match
///         <see cref="TestControlPlaneOptions.SharedSecret"/> → 401 on mismatch</item>
/// </list>
///
/// <para>
/// 404 (not 403) for the flag-off case: the production surface is
/// indistinguishable from a route that does not exist, even if an attacker
/// knows the path.
/// </para>
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = false, Inherited = true)]
public sealed class TestControlPlaneOnlyAttribute : Attribute, IAsyncActionFilter
{
    /// <summary>Header name carrying the shared secret.</summary>
    public const string SecretHeaderName = "X-Test-Control-Plane-Secret";

    /// <inheritdoc />
    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        var options = context.HttpContext.RequestServices
            .GetRequiredService<IOptionsMonitor<TestControlPlaneOptions>>()
            .CurrentValue;

        if (!options.Enabled)
        {
            context.Result = new NotFoundResult();
            return;
        }

        // Fail-closed even when enabled: an unconfigured or empty secret must not
        // allow unauthenticated access to the clock/job-runner surface.
        if (string.IsNullOrWhiteSpace(options.SharedSecret))
        {
            context.Result = new UnauthorizedResult();
            return;
        }

        var provided = context.HttpContext.Request.Headers[SecretHeaderName].ToString();
        if (!string.Equals(provided, options.SharedSecret, StringComparison.Ordinal))
        {
            context.Result = new UnauthorizedResult();
            return;
        }

        await next();
    }
}
