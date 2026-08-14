using System;
using Microsoft.AspNetCore.Mvc.ActionConstraints;
using Microsoft.Extensions.DependencyInjection;

namespace JeebGateway.Realtime;

/// <summary>Constrains a route's {tenant} segment to <see cref="RealtimeTopicNames"/>'
/// accepted set, so unknown-tenant URLs 404 before auth, as the old literal routes did.</summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class AcceptedRealtimeTenantAttribute : Attribute, IActionConstraint
{
    public int Order => 0;

    public bool Accept(ActionConstraintContext context)
    {
        var tenant = context.RouteContext.RouteData.Values["tenant"] as string;
        var names = context.RouteContext.HttpContext.RequestServices
            .GetRequiredService<RealtimeTopicNames>();
        return names.IsAcceptedTenant(tenant);
    }
}
