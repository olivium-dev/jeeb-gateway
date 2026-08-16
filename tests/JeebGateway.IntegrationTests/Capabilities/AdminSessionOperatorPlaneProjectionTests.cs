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

/// <summary>
/// OA-39 — GET /admin/v1/session must project the OPERATOR-PLANE capabilities the caller's roles
/// hold, not the subset whose name starts with <c>admin.</c>. The prefix is a naming convention:
/// the ADR-005 §K/§M operator capabilities (<c>kyc.review</c>, <c>users.admin.manage</c>,
/// <c>wallet.manage</c>, <c>finance.read</c>, …) do not carry it, so a real admin's session omitted
/// every one of them and the CMS panels gated on them rendered "you do not have access".
/// </summary>
public sealed class AdminSessionOperatorPlaneProjectionTests
{
    /// <summary>The gate the gateway actually enforces on GET /admin/kyc/queue and PATCH /admin/kyc/{id}/review.</summary>
    private const string KycReviewCapability = Capabilities.KycReview;

    [Fact]
    public async Task AdminSession_ProjectsTheCapabilityThatGatesKycReview()
    {
        using var factory = Factory();

        var session = await SessionFor(factory, Roles.Admin);

        session.Capabilities.Should().Contain(KycReviewCapability,
            "admin holds kyc.review in CapabilityRolePolicy.Map and it gates the KYC review routes");
    }

    [Fact]
    public async Task AdminSession_ProjectsTheOtherBackOfficeCapabilitiesAdminHolds()
    {
        using var factory = Factory();

        var session = await SessionFor(factory, Roles.Admin);

        session.Capabilities.Should().Contain(new[]
        {
            Capabilities.UsersAdminManage,
            Capabilities.WalletManage,
            Capabilities.FinanceRead,
            Capabilities.SettlementsManage,
            Capabilities.ZonesManage,
            Capabilities.TiersManage,
        });
    }

    // NEGATIVE CONTROL 1 (role). Same probe, same assertion, different role -> different answer.
    // `support` reaches the endpoint (it holds admin.portal.access) but must NOT hold kyc.review.
    [Fact]
    public async Task SupportSession_ReachesTheEndpointButNeverReceivesKycReview()
    {
        using var factory = Factory();

        var support = await SessionFor(factory, Roles.Support);
        var admin = await SessionFor(factory, Roles.Admin);

        support.Capabilities.Should().NotContain(KycReviewCapability);
        support.Capabilities.Should().Contain(Capabilities.AdminPortalAccess,
            "the control must be a session that really did reach the projection, not a 403");

        // The same assertion on the same probe returns the opposite answer for admin, so the
        // control above is capable of failing — it is not asserting on an empty/blocked response.
        admin.Capabilities.Should().Contain(KycReviewCapability);
    }

    // NEGATIVE CONTROL 2 (over-projection). Admin genuinely HOLDS these participant-plane
    // capabilities in the map, so this fails the moment the filter is dropped instead of widened.
    [Fact]
    public async Task AdminSession_NeverProjectsParticipantPlaneCapabilities()
    {
        using var factory = Factory();

        var session = await SessionFor(factory, Roles.Admin);

        foreach (var held in new[]
        {
            Capabilities.ProfileReadSelf,
            Capabilities.ProfileWriteSelf,
            Capabilities.DataExportSelf,
            Capabilities.NotificationPrefsSelf,
            Capabilities.NotificationsReadSelf,
            Capabilities.AuthLogoutSelf,
            Capabilities.DisputeReadMine,
            Capabilities.SupportCreateSelf,
            Capabilities.SupportReadOwn,
            Capabilities.DeliveryTrackOwn,
        })
        {
            CapabilityRolePolicy.RolesFor(held).Should().Contain(Roles.Admin,
                "the control is only meaningful for capabilities an admin really holds");
            session.Capabilities.Should().NotContain(held);
        }

        session.Capabilities.Should().NotContain(new[]
        {
            Capabilities.RequestCreate,
            Capabilities.OfferSubmit,
            Capabilities.ChatRead,
            Capabilities.WalletReadOwn,
        });
    }

    // NEGATIVE CONTROL 3 (role separation preserved). Widening the projection must not turn a
    // plain admin into a delivery/finance mutation superuser, nor admit an end user at all.
    [Fact]
    public async Task AdminIsStillNotADeliveryOrFinanceSuperuser_AndEndUsersAreStillRefused()
    {
        using var factory = Factory();

        var admin = await SessionFor(factory, Roles.Admin);
        admin.Capabilities.Should().NotContain(Capabilities.AdminDeliveriesOperate);
        admin.Capabilities.Should().NotContain(Capabilities.AdminSettlementsManage);

        var operations = await SessionFor(factory, Roles.Operations);
        operations.Capabilities.Should().Contain(Capabilities.AdminDeliveriesOperate,
            "the two assertions above are capable of returning the opposite answer");
        var financeApprover = await SessionFor(factory, Roles.FinanceApprover);
        financeApprover.Capabilities.Should().Contain(Capabilities.AdminSettlementsManage);

        var customer = factory.CreateClient()
            .WithBearer(CapabilityTestHarness.MintExternalOperatorBearer(factory, Roles.Client));
        (await customer.GetAsync("/admin/v1/session")).StatusCode.Should()
            .Be(HttpStatusCode.Forbidden);
    }

    // Pins the projected set so any future change to the operator plane is a deliberate diff
    // on a permissions boundary rather than a silent side effect of adding a capability.
    [Fact]
    public async Task AdminSession_ProjectsExactlyTheOperatorPlaneCapabilitiesAdminHolds()
    {
        using var factory = Factory();

        var session = await SessionFor(factory, Roles.Admin);

        var expected = CapabilityRolePolicy.OperatorPlane
            .Where(capability => CapabilityRolePolicy.RolesFor(capability)
                .Contains(Roles.Admin, StringComparer.OrdinalIgnoreCase))
            .OrderBy(capability => capability, StringComparer.Ordinal)
            .ToArray();

        session.Capabilities.Should().BeEquivalentTo(expected);
        expected.Should().Contain(KycReviewCapability);
        foreach (var capability in expected)
            CapabilityRolePolicy.IsParticipantPlane(capability).Should().BeFalse();
    }

    private static async Task<AdminSessionResponse> SessionFor(
        WebApplicationFactory<Program> factory, string role)
    {
        var client = factory.CreateClient()
            .WithBearer(CapabilityTestHarness.MintExternalOperatorBearer(factory, role));
        var response = await client.GetAsync("/admin/v1/session");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var session = await response.Content.ReadFromJsonAsync<AdminSessionResponse>();
        session.Should().NotBeNull();
        return session!;
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
