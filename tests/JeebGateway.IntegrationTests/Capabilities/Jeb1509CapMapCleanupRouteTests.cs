using System.Net;
using FluentAssertions;
using JeebGateway.IntegrationTests.Infrastructure;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace JeebGateway.IntegrationTests.CapabilityAuthz;

/// <summary>
/// JEB-1509 cap-map cleanup — END-TO-END route-level ALLOW/DENY for the three tightenings, through
/// the real <c>Program</c> pipeline (WebApplicationFactory). The data-driven
/// <see cref="PerCapabilityAllowDenyTests"/> already proves the policy-level allow/deny for every
/// capability (including the new offer.accept/push.broadcast/contract.* rows); these add the explicit
/// route-wiring assertions the ticket requires for the LIVE-behaviour-changing tightenings:
///
/// <list type="bullet">
///   <item><c>POST /api/PushNotification/broadcast</c> → push.broadcast {admin}: admin reaches the
///   controller; a non-admin (driver) is a hard 403 (TIGHTENING — was any-auth).</item>
///   <item><c>POST /contract-signing/templates</c> and <c>POST /contract-signing/contracts</c> →
///   contract.template.manage {admin}: admin reaches the controller; a non-admin is 403
///   (TIGHTENING — were L1-only public writes).</item>
///   <item><c>POST /contract-signing/contracts/{id}/signatures</c> → contract.sign {client, jeeber}:
///   a client AND a jeeber reach the controller (end-user ToS acceptance); an admin-only token is
///   403 (admin is not an end-user signer).</item>
/// </list>
///
/// "Reached the controller" = <c>NotBe(401).And.NotBe(403)</c>. The contract-signing upstream flag
/// defaults OFF so an authorized caller resolves to a 400 (validation) or 503 (UpstreamDisabled) —
/// both prove BOTH auth layers passed (ADR-005 matrix §0 rule 2). Tokens are REAL minted
/// gateway-session JWTs; admin is authorized purely from the 'admin' role claim (no separate scheme).
/// </summary>
public sealed class Jeb1509CapMapCleanupRouteTests
{
    private const string PushBroadcastRoute = "/api/PushNotification/broadcast";
    private const string RegisterTemplateRoute = "/contract-signing/templates";
    private const string CreateContractRoute = "/contract-signing/contracts";
    private const string SignRoute = "/contract-signing/contracts/ctr_test/signatures";

    private static StringContent EmptyJson()
        => new("{}", System.Text.Encoding.UTF8, "application/json");

    private static void AssertReachedController(HttpResponseMessage resp)
    {
        resp.StatusCode.Should().NotBe(HttpStatusCode.Unauthorized,
            "Layer 1 (audience) passed — a valid aud=jeeb-clients caller");
        resp.StatusCode.Should().NotBe(HttpStatusCode.Forbidden,
            "Layer 2 (capability) passed — the user type holds the capability; the request reached the controller");
    }

    // ── push.broadcast {admin} (TIGHTENING — was any-auth) ─────────────────────────────────────────

    [Fact]
    public async Task PushBroadcast_Admin_ReachesController()
    {
        using var f = new WebApplicationFactory<Program>();
        var client = f.CreateClient().WithBearer(CapabilityTestHarness.MintBearer(f, "admin"));

        var resp = await client.PostAsync(PushBroadcastRoute, EmptyJson());

        AssertReachedController(resp); // admin holds push.broadcast → reaches the broadcast handler
    }

    [Theory]
    [InlineData("driver")]   // opaque jeeber
    [InlineData("customer")] // opaque client
    public async Task PushBroadcast_NonAdmin_Is403(string role)
    {
        using var f = new WebApplicationFactory<Program>();
        var client = f.CreateClient().WithBearer(CapabilityTestHarness.MintBearer(f, role));

        var resp = await client.PostAsync(PushBroadcastRoute, EmptyJson());

        await CapabilityTestHarness.AssertForbiddenCapabilityBody(resp); // fleet-wide broadcast is admin-only now
    }

    [Fact]
    public async Task PushBroadcast_Unauthenticated_Is401()
    {
        using var f = new WebApplicationFactory<Program>();
        var client = f.CreateClient(); // no bearer

        var resp = await client.PostAsync(PushBroadcastRoute, EmptyJson());

        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized, "L1 owns the 401 on an unauthenticated caller");
    }

    // ── contract.template.manage {admin} — RegisterTemplate + CreateContract (TIGHTENING) ──────────

    [Fact]
    public async Task RegisterTemplate_Admin_ReachesController()
    {
        using var f = new WebApplicationFactory<Program>();
        var client = f.CreateClient().WithBearer(CapabilityTestHarness.MintBearer(f, "admin"));

        var resp = await client.PostAsync(RegisterTemplateRoute, EmptyJson());

        AssertReachedController(resp); // admin → 400 (validation) or 503 (flag off), never 403
    }

    [Theory]
    [InlineData("customer")]
    [InlineData("driver")]
    public async Task RegisterTemplate_NonAdmin_Is403(string role)
    {
        using var f = new WebApplicationFactory<Program>();
        var client = f.CreateClient().WithBearer(CapabilityTestHarness.MintBearer(f, role));

        var resp = await client.PostAsync(RegisterTemplateRoute, EmptyJson());

        await CapabilityTestHarness.AssertForbiddenCapabilityBody(resp); // template authoring is admin-only
    }

    [Fact]
    public async Task CreateContract_Admin_ReachesController()
    {
        using var f = new WebApplicationFactory<Program>();
        var client = f.CreateClient().WithBearer(CapabilityTestHarness.MintBearer(f, "admin"));

        var resp = await client.PostAsync(CreateContractRoute, EmptyJson());

        AssertReachedController(resp);
    }

    [Theory]
    [InlineData("customer")]
    [InlineData("driver")]
    public async Task CreateContract_NonAdmin_Is403(string role)
    {
        using var f = new WebApplicationFactory<Program>();
        var client = f.CreateClient().WithBearer(CapabilityTestHarness.MintBearer(f, role));

        var resp = await client.PostAsync(CreateContractRoute, EmptyJson());

        await CapabilityTestHarness.AssertForbiddenCapabilityBody(resp);
    }

    // ── contract.sign {client, jeeber} — end-user ToS acceptance ───────────────────────────────────

    [Theory]
    [InlineData("customer")] // opaque client
    [InlineData("client")]   // canonical client
    [InlineData("driver")]   // opaque jeeber
    [InlineData("jeeber")]   // canonical jeeber
    public async Task Sign_ClientOrJeeber_ReachesController(string role)
    {
        using var f = new WebApplicationFactory<Program>();
        var client = f.CreateClient().WithBearer(CapabilityTestHarness.MintBearer(f, role));

        var resp = await client.PostAsync(SignRoute, EmptyJson());

        AssertReachedController(resp); // end-user signer holds contract.sign → reaches the Sign handler
    }

    [Fact]
    public async Task Sign_AdminOnly_Is403_AdminIsNotAnEndUserSigner()
    {
        using var f = new WebApplicationFactory<Program>();
        var client = f.CreateClient().WithBearer(CapabilityTestHarness.MintBearer(f, "admin"));

        var resp = await client.PostAsync(SignRoute, EmptyJson());

        await CapabilityTestHarness.AssertForbiddenCapabilityBody(resp); // contract.sign is {client, jeeber}, not admin
    }
}
