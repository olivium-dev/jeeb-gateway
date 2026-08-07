using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using JeebGateway.Auth.Capabilities;
using JeebGateway.Controllers;
using JeebGateway.IntegrationTests.Infrastructure;
using JeebGateway.Users;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace JeebGateway.IntegrationTests.CapabilityAuthz;

public sealed class AdminEssentialCapabilityTests
{
    [Fact]
    public async Task Session_RejectsUnauthenticatedAndEndUserRoles()
    {
        using var factory = Factory();

        (await factory.CreateClient().GetAsync("/admin/v1/session"))
            .StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        var customer = factory.CreateClient()
            .WithBearer(CapabilityTestHarness.MintBearer(factory, Roles.Client));
        (await customer.GetAsync("/admin/v1/session"))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Session_ReturnsExactSupportCapabilities()
    {
        using var factory = Factory();
        var client = factory.CreateClient()
            .WithBearer(CapabilityTestHarness.MintExternalOperatorBearer(factory, Roles.Support));

        var response = await client.GetAsync("/admin/v1/session");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var session = await response.Content.ReadFromJsonAsync<AdminSessionResponse>();
        session.Should().NotBeNull();
        session!.Capabilities.Should().Contain(new[]
        {
            Capabilities.AdminPortalAccess,
            Capabilities.AdminCasesRead,
            Capabilities.AdminCasesUpdate,
            Capabilities.AdminDeliveriesRead,
        });
        session.Capabilities.Should().NotContain(new[]
        {
            Capabilities.AdminCasesClose,
            Capabilities.AdminDeliveriesOperate,
            Capabilities.AdminSettlementsRead,
            Capabilities.AdminSettlementsManage,
        });
        response.Headers.CacheControl!.NoStore.Should().BeTrue();
    }

    [Fact]
    public async Task GenericAdmin_IsNotFinanceOrDeliveryMutationSuperuser()
    {
        using var factory = Factory();
        var client = factory.CreateClient()
            .WithBearer(CapabilityTestHarness.MintExternalOperatorBearer(factory, Roles.Admin));

        var session = await (await client.GetAsync("/admin/v1/session"))
            .Content.ReadFromJsonAsync<AdminSessionResponse>();

        session!.Capabilities.Should().Contain(Capabilities.AdminCasesClose);
        session.Capabilities.Should().Contain(Capabilities.AdminSettlementsRead);
        session.Capabilities.Should().NotContain(Capabilities.AdminDeliveriesOperate);
        session.Capabilities.Should().NotContain(Capabilities.AdminSettlementsManage);
        session.ClosurePolicy.CustomerConfirmationRequired.Should().BeFalse();
        session.ClosurePolicy.RequiredCapability.Should().Be(Capabilities.AdminCasesClose);
    }

    [Fact]
    public async Task SupportCannotCloseCase_ButSupportLeadPassesAuthorization()
    {
        using var factory = Factory();
        var support = factory.CreateClient()
            .WithBearer(CapabilityTestHarness.MintExternalOperatorBearer(factory, Roles.Support));
        var lead = factory.CreateClient()
            .WithBearer(CapabilityTestHarness.MintExternalOperatorBearer(factory, Roles.SupportLead));
        using var body = JsonContent.Create(new { expectedVersion = 1, reason = "resolved" });

        var denied = await support.PostAsync("/admin/v1/cases/00000000-0000-0000-0000-000000000001/close", body);
        denied.StatusCode.Should().Be(HttpStatusCode.Forbidden);

        using var leadBody = JsonContent.Create(new { expectedVersion = 1, reason = "resolved" });
        lead.DefaultRequestHeaders.Add("Idempotency-Key", Guid.NewGuid().ToString("N"));
        var allowed = await lead.PostAsync(
            "/admin/v1/cases/00000000-0000-0000-0000-000000000001/close", leadBody);
        allowed.StatusCode.Should().NotBe(HttpStatusCode.Unauthorized);
        allowed.StatusCode.Should().NotBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task AdminCapabilitiesRejectOrdinaryGatewayAndTrustedEdgeIdentities()
    {
        using var factory = Factory();
        var ordinary = factory.CreateClient()
            .WithBearer(CapabilityTestHarness.MintBearer(factory, Roles.Support));
        (await ordinary.GetAsync("/admin/v1/session")).StatusCode.Should()
            .Be(HttpStatusCode.Forbidden);

        var edge = factory.CreateClient();
        edge.DefaultRequestHeaders.Add("X-User-Id", CapabilityTestHarness.UserId);
        edge.DefaultRequestHeaders.Add("X-User-Roles", Roles.Support);
        (await edge.GetAsync("/admin/v1/session")).StatusCode.Should()
            .BeOneOf(HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden);
    }

    private static WebApplicationFactory<Program> Factory() =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            builder.ConfigureAppConfiguration((_, configuration) =>
                configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["AdminOidc:Enabled"] = "true",
                    ["AdminOidc:Issuer"] = "https://identity.example.test",
                    ["AdminOidc:AuthorizationEndpoint"] = "https://identity.example.test/authorize",
                    ["AdminOidc:TokenEndpoint"] = "https://identity.example.test/token",
                    ["AdminOidc:JwksUri"] = "https://identity.example.test/jwks",
                    ["AdminOidc:ClientId"] = "jeeb-admin",
                    ["AdminOidc:ClientSecret"] = "test-client-secret",
                    ["AdminOidc:RedirectUri"] = "https://admin.jeeb.example/gateway/admin/v1/auth/oidc/callback",
                    ["AdminOidc:StateProtectionKey"] = Convert.ToBase64String(new byte[32]),
                    ["AdminOidc:RoleMappings:support:0"] = "support",
                })));
}
