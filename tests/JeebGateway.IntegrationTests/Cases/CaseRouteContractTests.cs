using FluentAssertions;
using JeebGateway.Controllers;
using JeebGateway.Controllers.V1;
using Microsoft.AspNetCore.Mvc.Routing;
using Xunit;

namespace JeebGateway.IntegrationTests.Cases;

public sealed class CaseRouteContractTests
{
    [Fact]
    public void Public_Dispute_And_Support_Routes_Are_Complete()
    {
        Routes<DisputeCasesController>().Should().Contain(new[]
        {
            "v1/disputes",
            "v1/deliveries/{deliveryId}/escalate",
            "v1/deliveries/{deliveryId}/disputes/evidence-preview",
            "v1/disputes/{id}",
            "v1/disputes/{id}/reply",
        });
        Routes<JeebSupportController>().Should().Contain(new[]
        {
            "tickets",
            "tickets/{id}",
            "tickets/{id}/messages",
            "tickets/{id}/reply",
            "categories",
        });
        Routes<JeebSupportController>().Should().NotContain(route =>
            route.EndsWith("/replies", StringComparison.Ordinal));
    }

    [Fact]
    public void Admin_Generic_Queue_And_All_Case_Commands_Are_Routed()
    {
        var routes = Routes<AdminCasesController>();
        routes.Should().Contain("admin/v1/cases");
        routes.Should().Contain("admin/v1/disputes");
        routes.Should().Contain("admin/v1/support/tickets");
        routes.Should().Contain("admin/v1/cases/{id}");
        foreach (var command in new[]
                 { "claim", "reassign", "priority", "due", "reply", "note", "mark-fixed", "close", "reopen" })
        {
            routes.Should().Contain($"admin/v1/cases/{{id}}/{command}");
            routes.Should().Contain($"admin/v1/disputes/{{id}}/{command}");
            routes.Should().Contain($"admin/v1/support/tickets/{{id}}/{command}");
        }
    }

    private static IReadOnlyList<string> Routes<T>() => typeof(T).GetMethods()
        .SelectMany(method => method.GetCustomAttributes(inherit: true).OfType<HttpMethodAttribute>())
        .Select(attribute => attribute.Template)
        .Where(template => template is not null)
        .Cast<string>()
        .ToArray();
}
