using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace JeebGateway.Security;

/// <summary>
/// Action filter that gates an endpoint (or an entire controller) behind the
/// <c>Features:DevEndpoints:Enabled</c> flag (<see cref="DevEndpointOptions"/>).
///
/// <para>
/// When the flag is <b>off</b> (the default, and the committed value in EVERY
/// environment including production) the action short-circuits with a
/// <b>404 Not Found</b> — deliberately NOT 403 — so the production surface is
/// indistinguishable from a route that does not exist. No response body hints
/// that the route is real.
/// </para>
///
/// <para>
/// Keeping the gate in one filter (rather than branching inside each action)
/// means the 404 behaviour is implemented and tested in exactly one place, and
/// makes the additive nature of the change obvious: applying
/// <c>[DevOnly]</c> to the net-new <see cref="JeebGateway.Controllers.DevController"/>
/// is the whole gating story.
/// </para>
///
/// <para>
/// Resolves the flag through <see cref="IOptionsMonitor{TOptions}"/> so it can
/// be toggled by a configuration reload without a redeploy.
/// </para>
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = false, Inherited = true)]
public sealed class DevOnlyAttribute : Attribute, IAsyncActionFilter
{
    /// <inheritdoc />
    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        var options = context.HttpContext.RequestServices
            .GetRequiredService<IOptionsMonitor<DevEndpointOptions>>()
            .CurrentValue;

        if (!options.Enabled)
        {
            // Behave as if the route does not exist. 404 — not 403 — so the
            // production surface is indistinguishable from "no such endpoint".
            context.Result = new NotFoundResult();
            return;
        }

        await next();
    }
}
