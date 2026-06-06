using System.Reflection;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace JeebGateway.Auth.Capabilities;

/// <summary>
/// ADR-005 Layer 2 — the default-deny coverage guard. An in-repo startup REFLECTION SCAN over the
/// gateway's endpoint metadata (NOT a Roslyn analyzer — honors the no-shared-CI guardrail) that
/// asserts every controller action carries EITHER a <see cref="CapabilityRequirement"/> policy
/// (via <see cref="RequireCapabilityAttribute"/>) OR an explicit <see cref="PublicEndpointAttribute"/>.
///
/// <para>This is the user-type analogue of ADR-004's authn FallbackPolicy: ADR-004 guarantees an
/// identified caller; this guarantees a DECLARED capability or an EXPLICIT public opt-out — so no
/// action is ever silently authorized for all user types by omission.</para>
///
/// <para><b>Staging (one-shot build).</b> In the L2 CORE step, <c>CapabilityGuard:Enforce</c>
/// defaults to <c>false</c>: the guard runs and LOGS every uncovered action as a warning but does
/// NOT throw (the ~46 controllers are not yet annotated). The FINAL annotation step flips
/// <c>Enforce=true</c>, at which point an uncovered action fails startup/CI. A deliberately
/// un-annotated fixture action proves it fires.</para>
///
/// <para>Wired as an <see cref="IHostedService"/> (mirrors <c>BffStartupValidator</c>) so the signal
/// surfaces before the first request.</para>
/// </summary>
public sealed class CapabilityCoverageGuard : IHostedService
{
    private readonly EndpointDataSource _endpoints;
    private readonly IOptions<CapabilityGuardOptions> _options;
    private readonly ILogger<CapabilityCoverageGuard> _logger;

    public CapabilityCoverageGuard(
        EndpointDataSource endpoints,
        IOptions<CapabilityGuardOptions> options,
        ILogger<CapabilityCoverageGuard> logger)
    {
        _endpoints = endpoints;
        _options = options;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        Scan();
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>
    /// Enumerate every controller-action endpoint and collect those that have neither an
    /// authorization policy carrying a <see cref="CapabilityRequirement"/> nor a
    /// <see cref="PublicEndpointAttribute"/>. Exposed for unit tests so they can assert the
    /// guard's verdict without standing up the full host.
    /// </summary>
    /// <returns>The display names of uncovered actions (empty when fully covered).</returns>
    public IReadOnlyList<string> FindUncoveredActions()
    {
        var uncovered = new List<string>();

        foreach (var endpoint in _endpoints.Endpoints)
        {
            if (endpoint is not RouteEndpoint route)
            {
                continue;
            }

            // Only controller actions are in scope. Minimal-API/static endpoints are out of scope.
            var actionDescriptor = route.Metadata.GetMetadata<ControllerActionDescriptor>();
            if (actionDescriptor is null)
            {
                continue;
            }

            // Explicit public opt-out (method or class level) satisfies the guard.
            if (HasPublicMarker(route, actionDescriptor))
            {
                continue;
            }

            // A CapabilityRequirement on any authorization policy attached to the endpoint
            // satisfies the guard. We inspect both the resolved AuthorizationPolicy and any
            // IAuthorizeData (RequireCapabilityAttribute) referencing a cap:* policy.
            if (HasCapabilityRequirement(route))
            {
                continue;
            }

            uncovered.Add(actionDescriptor.DisplayName ?? route.DisplayName ?? actionDescriptor.ActionName);
        }

        return uncovered;
    }

    private void Scan()
    {
        var uncovered = FindUncoveredActions();
        if (uncovered.Count == 0)
        {
            _logger.LogInformation(
                "ADR-005 capability coverage guard: all controller actions carry [RequireCapability] or [PublicEndpoint].");
            return;
        }

        var list = string.Join(", ", uncovered);

        if (_options.Value.Enforce)
        {
            // Default-deny ENFORCED (final step): an uncovered action fails startup/CI.
            throw new CapabilityCoverageException(
                $"ADR-005 default-deny: {uncovered.Count} controller action(s) carry neither " +
                $"[RequireCapability] nor [PublicEndpoint]: {list}. Annotate each before enabling " +
                "CapabilityGuard:Enforce.");
        }

        // CORE step: report-only. The annotation sweep that follows reduces this to zero, after
        // which Enforce is flipped to true.
        _logger.LogWarning(
            "ADR-005 capability coverage guard (report-only; CapabilityGuard:Enforce=false): " +
            "{Count} controller action(s) not yet annotated with [RequireCapability]/[PublicEndpoint]: {Actions}",
            uncovered.Count, list);
    }

    private static bool HasPublicMarker(RouteEndpoint route, ControllerActionDescriptor action)
    {
        // Endpoint metadata first (covers attribute on the action method).
        if (route.Metadata.GetMetadata<PublicEndpointAttribute>() is not null)
        {
            return true;
        }

        // Method- and class-level reflection fallback (Inherited=true on the attribute).
        return action.MethodInfo.GetCustomAttribute<PublicEndpointAttribute>(inherit: true) is not null
            || action.ControllerTypeInfo.GetCustomAttribute<PublicEndpointAttribute>(inherit: true) is not null;
    }

    private static bool HasCapabilityRequirement(RouteEndpoint route)
    {
        // (1) A resolved AuthorizationPolicy that includes a CapabilityRequirement.
        foreach (var policy in route.Metadata.GetOrderedMetadata<AuthorizationPolicy>())
        {
            if (policy.Requirements.OfType<CapabilityRequirement>().Any())
            {
                return true;
            }
        }

        // (2) A RequireCapabilityAttribute (IAuthorizeData with a cap:* policy name) on the route —
        // covers the common case where the per-cap policy is resolved by name at request time.
        foreach (var authorizeData in route.Metadata.GetOrderedMetadata<IAuthorizeData>())
        {
            if (authorizeData is RequireCapabilityAttribute)
            {
                return true;
            }

            if (!string.IsNullOrEmpty(authorizeData.Policy)
                && authorizeData.Policy.StartsWith("cap:", StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }
}

/// <summary>
/// Distinct exception so operators / CI can match the default-deny coverage failure specifically.
/// </summary>
public sealed class CapabilityCoverageException : Exception
{
    public CapabilityCoverageException(string message) : base(message) { }
}
