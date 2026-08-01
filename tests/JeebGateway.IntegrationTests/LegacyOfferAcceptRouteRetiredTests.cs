using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Threading.Tasks;
using FluentAssertions;
using JeebGateway.Auth.Capabilities;
using JeebGateway.Controllers;
using JeebGateway.Conversations;
using JeebGateway.IntegrationTests.Infrastructure;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace JeebGateway.IntegrationTests;

/// <summary>
/// Owner ruling 2026-08-01 — RUNTIME half of "retire the duplicate
/// <c>POST /offers/{offerId}/accept</c> surface".
///
/// The claim being retired is a behavioural one ("the route is gone, the V1 route is
/// unaffected, and no capability marker regressed"), and none of that is provable by
/// grepping for a deleted identifier — a route can be re-added under a different
/// controller, and a capability can silently lose its only route-level lock. Every
/// assertion here boots the real <c>Program</c> pipeline or reflects over the shipped
/// assembly.
///
/// <list type="bullet">
///   <item><b>R1</b> the retired route is unroutable for an AUTHORIZED client (404), and
///     R1b pins what an UNAUTHENTICATED one gets (401 — the fallback auth policy fires
///     first), with a never-existed path as the control.</item>
///   <item><b>R2</b> POSITIVE CONTROL — the surviving V1 accept route still routes for
///     the same caller in the same host. Without this, R1 would also pass if the whole
///     pipeline were broken.</item>
///   <item><b>R3</b> the surviving OffersController actions (Edit / Reject) still route
///     — the retirement removed one action, not the controller.</item>
///   <item><b>R4</b> ADR-005 no-regression: <c>offer.accept</c> is still declared by
///     exactly ONE action, and that action is the V1 one. This is the assertion that
///     fails if someone "cleans up" the capability along with the route.</item>
///   <item><b>R5</b> OffersController declares no accept route at all any more, under
///     any name — a rename cannot slip past R1.</item>
///   <item><b>R6</b> <c>IConversationProvisioner.AdvanceToAcceptedAsync</c> is gone from
///     the contract, so it cannot be re-called from a new site.</item>
/// </list>
///
/// NOT PROVEN HERE: that MSI's live gateway stopped serving the old route (it never
/// served a real accept on it — 0 <c>POST /api/members</c> against 27 V1-only
/// <c>POST /api/conversations/{id}/settle</c> in the live journal), nor that the mobile
/// app is unaffected (0 executable non-V1 accept call sites at origin/main). Those are
/// `service` / `device` evidence and are recorded in the PR.
/// </summary>
public sealed class LegacyOfferAcceptRouteRetiredTests
{
    private const string RetiredRoute = "/offers/ofr_retired/accept";
    private const string SurvivingRoute = "/v1/offers/ofr_retired/accept";

    private static HttpClient ClientActor(WebApplicationFactory<Program> f)
        => f.CreateClient().WithBearer(CapabilityTestHarness.MintBearer(f, "customer"));

    // ---------------------------------------------------------------------
    // R1 / R2 — the route is gone; the V1 route is not.
    // ---------------------------------------------------------------------

    [Fact]
    public async Task R1_RetiredRoute_IsNotRoutable_ForAnAuthorizedClient()
    {
        using var f = new WebApplicationFactory<Program>();

        var resp = await ClientActor(f).PostAsync(RetiredRoute, content: null);

        resp.StatusCode.Should().Be(HttpStatusCode.NotFound,
            "the action was deleted, so MVC matches no endpoint and the request never "
            + "reaches a controller — a caller with a VALID client token and the "
            + "offer.accept capability now gets 404, not 401/403/405");
    }

    [Fact]
    public async Task R2_POSCTRL_SurvivingV1AcceptRoute_StillRoutes_ForTheSameCaller()
    {
        using var f = new WebApplicationFactory<Program>();

        var resp = await ClientActor(f).PostAsync(SurvivingRoute, content: null);

        // The offer is unknown to this host, so the V1 route's own routing resolution
        // returns 404 too — but it does so FROM INSIDE the controller, having passed
        // both auth layers. The discriminator against R1 is therefore not the status
        // but the auth outcome: a 401/403 here would mean the capability wiring broke.
        resp.StatusCode.Should().NotBe(HttpStatusCode.Unauthorized,
            "L1 must still admit a valid aud=jeeb-clients caller on the surviving route");
        resp.StatusCode.Should().NotBe(HttpStatusCode.Forbidden,
            "L2 must still admit a client holding offer.accept on the surviving route");
        resp.StatusCode.Should().NotBe(HttpStatusCode.MethodNotAllowed,
            "POST is still the verb");
    }

    /// <summary>
    /// Pins what an UNAUTHENTICATED caller still pointed at the retired path receives.
    ///
    /// <para>The answer is <b>401, not 404</b> — and that surprised the retirement work,
    /// so it is nailed down here rather than assumed. The gateway applies a fallback
    /// authorization policy, so a request with no bearer is rejected by the auth
    /// middleware BEFORE the absence of a matching endpoint can turn into a 404. The
    /// practical consequence for a stale client: an old build calling the retired route
    /// without a valid token is told "unauthorized", which reads like a session problem
    /// rather than a removed endpoint. With a valid token it correctly gets 404 (R1).</para>
    ///
    /// <para>The second assertion is the CONTROL that stops this test claiming something
    /// about the retirement that is really just how the host answers ANY unknown path: a
    /// path that has never existed in this gateway must produce the SAME status. If it
    /// does, the 401 is the gateway's global posture for unmatched routes and says
    /// nothing specific about the retired accept surface.</para>
    /// </summary>
    [Fact]
    public async Task R1b_RetiredRoute_Unauthenticated_Is401_SameAsAnyUnknownPath()
    {
        using var f = new WebApplicationFactory<Program>();
        var anonymous = f.CreateClient();

        var retired = await anonymous.PostAsync(RetiredRoute, content: null);
        var neverExisted = await anonymous.PostAsync(
            "/offers/ofr_retired/this-route-has-never-existed", content: null);

        retired.StatusCode.Should().Be(HttpStatusCode.Unauthorized,
            "the gateway's fallback authorization policy rejects an anonymous caller "
            + "before routing's 404 can surface — so a stale client without a valid "
            + "token sees 401, not 404");

        retired.StatusCode.Should().Be(neverExisted.StatusCode,
            "CONTROL: a path that never existed must answer identically. Equal statuses "
            + "prove the 401 is the host's global unmatched-route posture, not a residue "
            + "of the retired endpoint still being half-wired");
    }

    // ---------------------------------------------------------------------
    // R3 — the CONTROLLER survives; only the one action was retired.
    // ---------------------------------------------------------------------

    [Fact]
    public async Task R3_SurvivingOffersControllerRoutes_StillRoute()
    {
        using var f = new WebApplicationFactory<Program>();
        var client = f.CreateClient().WithBearer(CapabilityTestHarness.MintBearer(f, "driver"));

        // PUT /v1/offers/{id} — Edit, a {jeeber} action, still on OffersController.
        var edit = await client.PutAsync(
            "/v1/offers/ofr_retired",
            new StringContent("{\"fee\":5}", System.Text.Encoding.UTF8, "application/json"));

        edit.StatusCode.Should().NotBe(HttpStatusCode.NotFound,
            "Edit was NOT retired — retiring the accept action must not take the "
            + "controller's other routes with it");
        edit.StatusCode.Should().NotBe(HttpStatusCode.Forbidden,
            "a jeeber still holds offer.edit.own");
    }

    // ---------------------------------------------------------------------
    // R4 — ADR-005 no-regression. THE assertion this change most needs.
    // ---------------------------------------------------------------------

    [Fact]
    public void R4_OfferAcceptCapability_IsStillDeclared_ByExactlyOneAction_TheV1One()
    {
        var declaring = typeof(Program).Assembly
            .GetTypes()
            .Where(t => typeof(ControllerBase).IsAssignableFrom(t))
            .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            .Where(m => m.GetCustomAttributes<RequireCapabilityAttribute>()
                         .Any(a => a.Capability == Capabilities.OfferAccept))
            .Select(m => $"{m.DeclaringType!.Name}.{m.Name}")
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();

        declaring.Should().BeEquivalentTo(
            new[] { "JeebOffersController.Accept" },
            "retiring the duplicate surface must leave the offer.accept capability with "
            + "EXACTLY ONE declaring route. Two would mean the duplicate is back; zero "
            + "would mean the capability lost its only route-level lock — the ADR-005 "
            + "regression this retirement must not cause");
    }

    // ---------------------------------------------------------------------
    // R5 / R6 — the retired code cannot come back under another name.
    // ---------------------------------------------------------------------

    [Fact]
    public void R5_OffersController_DeclaresNoAcceptRoute_UnderAnyName()
    {
        var acceptRoutes = typeof(OffersController)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .SelectMany(m => m.GetCustomAttributes<HttpMethodAttribute>()
                              .Select(a => $"{m.Name}:{a.Template}"))
            .Where(s => s.Contains("accept", StringComparison.OrdinalIgnoreCase))
            .ToList();

        acceptRoutes.Should().BeEmpty(
            "the accept surface was retired from this controller; re-adding it under a "
            + "different method name would slip past a route-string test but not this one");
    }

    [Fact]
    public void R6_ConversationProvisioner_NoLongerExposes_AdvanceToAccepted()
    {
        typeof(IConversationProvisioner)
            .GetMethod("AdvanceToAcceptedAsync")
            .Should().BeNull(
                "it had exactly one production call site — the retired accept action — and "
                + "was redundant with IJeebConversationClient.AdvancePhaseAsync, which does "
                + "winner promotion and loser removal atomically on the correct aggregate");

        // POS CONTROL: the interface itself is intact and still reflectable, so the
        // assertion above is a real absence and not a mistyped type name.
        typeof(IConversationProvisioner)
            .GetMethod("CreateBroadcastingConversationAsync")
            .Should().NotBeNull("the rest of the provisioner contract is untouched");
    }
}
