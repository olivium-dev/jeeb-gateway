using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace JeebGateway.Realtime;

/// <summary>Route constraint (ConstraintMap "realtimeTenant"): a {tenant} segment must be
/// in <see cref="RealtimeTopicNames"/>' accepted set, so unknown tenants 404 pre-auth.</summary>
public sealed class RealtimeTenantRouteConstraint : IRouteConstraint
{
    public const string Name = "realtimeTenant";

    public bool Match(
        HttpContext? httpContext,
        IRouter? route,
        string routeKey,
        RouteValueDictionary values,
        RouteDirection routeDirection)
    {
        // Resolved per-request, not per-pattern: constraint instances are made by the
        // route-pattern factory, which cannot constructor-inject services from DI.
        if (httpContext is null || !values.TryGetValue(routeKey, out var value))
        {
            return false;
        }

        return httpContext.RequestServices
            .GetRequiredService<RealtimeTopicNames>()
            .IsAcceptedTenant(value as string);
    }
}
