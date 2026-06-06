using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using JeebGateway.IntegrationTests.Infrastructure;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace JeebGateway.IntegrationTests.CapabilityAuthz;

/// <summary>
/// ADR-005 Layer 2 — END-TO-END route-level integration tests through the real <c>Program</c>
/// pipeline (WebApplicationFactory). These prove the 401-vs-403 WIRING that the policy-level
/// <see cref="PerCapabilityAllowDenyTests"/> cannot: the RFC7807 403 body, the Layer-1 401 vs
/// Layer-2 403 split, super-login through the REAL mint, the single dual-role token, and the
/// trusted-edge header path.
///
/// <para>Representative REAL routes (confirmed against the controllers):</para>
/// <list type="bullet">
///   <item><c>POST requests/{id}/offers</c> → <c>offer.submit</c> {jeeber}</item>
///   <item><c>POST requests</c> → <c>request.create</c> {client}</item>
///   <item><c>GET admin/kyc/queue</c> → <c>kyc.review</c> {admin}</item>
///   <item><c>GET admin/zones/online-jeebers</c> → <c>zones.manage</c> {admin}</item>
/// </list>
/// "Reached the controller" is asserted as <c>NotBe(401).And.NotBe(403)</c> — the upstream stub may
/// return 200/204/502/503, but a non-401/non-403 proves BOTH auth layers passed (matrix §0 rule 2).
/// </summary>
public sealed class CapabilityRouteIntegrationTests
{
    // ── Real routes ───────────────────────────────────────────────────────────────────────────────
    private const string OfferSubmitRoute = "/requests/req-test-1/offers"; // POST, offer.submit {jeeber}
    private const string RequestCreateRoute = "/requests";                  // POST, request.create {client}
    private const string KycReviewRoute = "/admin/kyc/queue";               // GET,  kyc.review {admin}
    private const string ZonesManageRoute = "/admin/zones/online-jeebers";  // GET,  zones.manage {admin}

    private static StringContent EmptyJson()
        => new("{}", System.Text.Encoding.UTF8, "application/json");

    private static void AssertReachedController(HttpResponseMessage resp)
    {
        resp.StatusCode.Should().NotBe(HttpStatusCode.Unauthorized,
            "Layer 1 (audience) passed — a valid aud=jeeb-clients caller");
        resp.StatusCode.Should().NotBe(HttpStatusCode.Forbidden,
            "Layer 2 (capability) passed — the user type holds the capability; the request reached the controller");
    }

    // ════════════════════════════════════════════════════════════════════════════════════════════
    // §2 — Load-bearing canonicalization (T1a ≡ T1b ; T1c ; T1d)
    // ════════════════════════════════════════════════════════════════════════════════════════════

    [Fact] // T1a
    public async Task OfferSubmit_OpaqueCustomerToken_Is403_NotL1_401()
    {
        using var f = new WebApplicationFactory<Program>();
        var client = f.CreateClient().WithBearer(CapabilityTestHarness.MintBearer(f, "customer"));

        var resp = await client.PostAsync(OfferSubmitRoute, EmptyJson());

        await CapabilityTestHarness.AssertForbiddenCapabilityBody(resp);
        resp.StatusCode.Should().NotBe(HttpStatusCode.Unauthorized,
            "canonicalized customer→client is a valid caller (L1 passes) but lacks the jeeber cap (L2 denies → 403)");
    }

    [Fact] // T1b
    public async Task OfferSubmit_CanonicalClientToken_Is403()
    {
        using var f = new WebApplicationFactory<Program>();
        var client = f.CreateClient().WithBearer(CapabilityTestHarness.MintBearer(f, "client"));

        var resp = await client.PostAsync(OfferSubmitRoute, EmptyJson());

        await CapabilityTestHarness.AssertForbiddenCapabilityBody(resp);
    }

    [Theory] // T1a ≡ T1b — the named structural gate
    [InlineData("customer")]
    [InlineData("client")]
    public async Task OfferSubmit_OpaqueAndCanonicalClient_ProduceIdenticalStatus(string role)
    {
        using var f = new WebApplicationFactory<Program>();
        var client = f.CreateClient().WithBearer(CapabilityTestHarness.MintBearer(f, role));

        var resp = await client.PostAsync(OfferSubmitRoute, EmptyJson());

        // Both vocabularies MUST 403. If the opaque and canonical forms ever diverged, the suite is
        // INVALID and this is an auto NO-GO (matrix §2 named gate).
        resp.StatusCode.Should().Be(HttpStatusCode.Forbidden,
            "the prod-opaque and canonical client tokens must yield the IDENTICAL deny on a jeeber cap");
    }

    [Fact] // T1c
    public async Task OfferSubmit_OpaqueDriverToken_ReachesController()
    {
        using var f = new WebApplicationFactory<Program>();
        var client = f.CreateClient().WithBearer(CapabilityTestHarness.MintBearer(f, "driver"));

        var resp = await client.PostAsync(OfferSubmitRoute, EmptyJson());

        AssertReachedController(resp); // driver→jeeber ∈ {jeeber} → ALLOW (catches "canonicalize denies all")
    }

    [Fact] // T1d
    public async Task RequestCreate_OpaqueCustomerToken_ReachesController()
    {
        using var f = new WebApplicationFactory<Program>();
        var client = f.CreateClient().WithBearer(CapabilityTestHarness.MintBearer(f, "customer"));

        var resp = await client.PostAsync(RequestCreateRoute, EmptyJson());

        AssertReachedController(resp); // opaque client authorizes its own cap
    }

    // ════════════════════════════════════════════════════════════════════════════════════════════
    // §3 — L1↔L2 separation (the 401-vs-403 tell)
    // ════════════════════════════════════════════════════════════════════════════════════════════

    [Fact] // T4
    public async Task WrongAudienceToken_OnCapabilityRoute_Is401_L2NeverRuns()
    {
        using var f = new WebApplicationFactory<Program>();
        // Signed with the trusted key but WRONG audience → Layer 1 rejects (401); Layer 2 never runs.
        var bearer = CapabilityTestHarness.MintWithAudience(f, "user-management", "admin");
        var client = f.CreateClient().WithBearer(bearer);

        var resp = await client.GetAsync(KycReviewRoute);

        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized, "Layer 1 owns 401 on a wrong-audience token");
        // L2 never ran → the body must NOT be the capability-forbidden type.
        if (resp.Content.Headers.ContentType?.MediaType == "application/problem+json")
        {
            var p = await resp.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
            if (p.TryGetProperty("type", out var t))
            {
                t.GetString().Should().NotBe("https://jeeb.dev/errors/forbidden-capability",
                    "a Layer-1 401 must never carry the Layer-2 capability-forbidden body");
            }
        }
    }

    [Fact] // T6
    public async Task Unauthenticated_OnCapabilityRoute_Is401()
    {
        using var f = new WebApplicationFactory<Program>();
        var client = f.CreateClient(); // no bearer, no X-User-Id

        var resp = await client.GetAsync(KycReviewRoute);

        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact] // SEP-1
    public async Task AuthenticatedEmptyRoleSet_OnJeeberRoute_Is403_Not401()
    {
        using var f = new WebApplicationFactory<Program>();
        // Valid aud=jeeb-clients, but ZERO roles claims: L1 passes, L2 denies.
        var client = f.CreateClient().WithBearer(CapabilityTestHarness.MintBearer(f /* no roles */));

        var resp = await client.PostAsync(OfferSubmitRoute, EmptyJson());

        await CapabilityTestHarness.AssertForbiddenCapabilityBody(resp);
        resp.StatusCode.Should().NotBe(HttpStatusCode.Unauthorized,
            "an authenticated caller with no roles is a Layer-2 403, never a Layer-1 401");
    }

    // ════════════════════════════════════════════════════════════════════════════════════════════
    // §4 — Super-login / admin (REAL mint via POST /auth/tokens, not hand-crafted)
    // ════════════════════════════════════════════════════════════════════════════════════════════

    [Fact] // SL-MINT / AC-SL1
    public async Task SuperLogin_WithValidKeyAndAdminRole_Mints_JeebClientsToken_WithAdminClaim()
    {
        using var f = CapabilityTestHarness.WithMintGate(CapabilityTestHarness.MintKey);
        var client = f.CreateClient();

        var accessToken = await CapabilityTestHarness.SuperLoginAsync(
            client, CapabilityTestHarness.MintKey, "admin");

        var jwt = new System.IdentityModel.Tokens.Jwt.JwtSecurityTokenHandler().ReadJwtToken(accessToken);
        jwt.Audiences.Should().Contain(CapabilityTestHarness.AudienceOf(f),
            "super-login mints a NORMAL gateway session token (aud=jeeb-clients), not a separate admin audience");
        jwt.Claims.Where(c => c.Type == "roles").Select(c => c.Value)
            .Should().Contain("admin", "the admin role the super-login requested is carried in the token");
    }

    [Fact] // T3 / SL-1
    public async Task SuperLoginAdmin_OnKycReview_ReachesController_NotForbidden()
    {
        using var f = CapabilityTestHarness.WithMintGate(CapabilityTestHarness.MintKey);
        var client = f.CreateClient();
        var bearer = await CapabilityTestHarness.SuperLoginAsync(
            f.CreateClient(), CapabilityTestHarness.MintKey, "admin");

        var resp = await client.WithBearer(bearer).GetAsync(KycReviewRoute);

        AssertReachedController(resp); // admin cap authorized purely from the 'admin' claim, normal audience
    }

    [Fact] // SL-3
    public async Task SuperLoginClient_OnAdminRoute_Is403_Not401()
    {
        using var f = CapabilityTestHarness.WithMintGate(CapabilityTestHarness.MintKey);
        var client = f.CreateClient();
        var bearer = await CapabilityTestHarness.SuperLoginAsync(
            f.CreateClient(), CapabilityTestHarness.MintKey, "client");

        var resp = await client.WithBearer(bearer).GetAsync(KycReviewRoute);

        await CapabilityTestHarness.AssertForbiddenCapabilityBody(resp);
    }

    [Fact] // SL-4
    public async Task Mint_WithoutServiceAuthKey_Is401()
    {
        using var f = CapabilityTestHarness.WithMintGate(CapabilityTestHarness.MintKey);
        var client = f.CreateClient();

        var resp = await client.PostAsync("/auth/tokens", EmptyJson()); // no X-Service-Auth-Key

        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized, "the mint gate (the privileged key) is the credential");
    }

    [Fact] // SL-4b
    public async Task Mint_WithWrongServiceAuthKey_Is403()
    {
        using var f = CapabilityTestHarness.WithMintGate(CapabilityTestHarness.MintKey);
        var client = f.CreateClient();
        client.DefaultRequestHeaders.Add(CapabilityTestHarness.MintHeader, "the-wrong-key");

        var resp = await client.PostAsync("/auth/tokens",
            System.Net.Http.Json.JsonContent.Create(new { userId = CapabilityTestHarness.UserId, roles = new[] { "admin" } }));

        resp.StatusCode.Should().Be(HttpStatusCode.Forbidden, "a wrong key is a 403, distinct from the no-key 401");
    }

    [Fact] // SL-6 / AC-SL6 — admin is NOT a superuser
    public async Task SuperLoginAdmin_OnJeeberRoute_Is403_AdminIsNotSuperuser()
    {
        using var f = CapabilityTestHarness.WithMintGate(CapabilityTestHarness.MintKey);
        var client = f.CreateClient();
        var bearer = await CapabilityTestHarness.SuperLoginAsync(
            f.CreateClient(), CapabilityTestHarness.MintKey, "admin");

        var resp = await client.WithBearer(bearer).PostAsync(OfferSubmitRoute, EmptyJson());

        await CapabilityTestHarness.AssertForbiddenCapabilityBody(resp); // admin claim does NOT hold jeeber caps
    }

    [Fact] // SL-5 — the super-login dev seam persists by design (no kill-flag added)
    public async Task SuperLogin_Seam_Persists_AndIsReachableWhenGated()
    {
        using var f = CapabilityTestHarness.WithMintGate(CapabilityTestHarness.MintKey);
        var client = f.CreateClient();

        // The [AllowAnonymous] mint endpoint still exists and mints when the privileged key is given.
        var resp = await client.SendAsync(MintRequest(CapabilityTestHarness.MintKey, "admin"));

        resp.StatusCode.Should().Be(HttpStatusCode.OK,
            "the super-login mint seam persists as a config-gated dev facility (owner decision #2)");
    }

    private static HttpRequestMessage MintRequest(string key, params string[] roles)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, "/auth/tokens")
        {
            Content = System.Net.Http.Json.JsonContent.Create(new { userId = CapabilityTestHarness.UserId, roles }),
        };
        req.Headers.Add(CapabilityTestHarness.MintHeader, key);
        return req;
    }

    // ════════════════════════════════════════════════════════════════════════════════════════════
    // §5 — Automatic upgrade: ONE token, no switch (T7)
    // ════════════════════════════════════════════════════════════════════════════════════════════

    [Fact] // T7a + T7b — the SAME token authorizes BOTH a client cap AND a jeeber cap
    public async Task DualRoleToken_SameToken_Authorizes_BothClientAndJeeberCaps_NoReMint()
    {
        using var f = new WebApplicationFactory<Program>();
        var bearer = CapabilityTestHarness.MintBearer(f, "customer", "driver"); // ONE token, OPAQUE dual

        var clientCapResp = await f.CreateClient().WithBearer(bearer).PostAsync(RequestCreateRoute, EmptyJson());
        var jeeberCapResp = await f.CreateClient().WithBearer(bearer).PostAsync(OfferSubmitRoute, EmptyJson());

        AssertReachedController(clientCapResp); // T7a — dual token holds the client cap
        AssertReachedController(jeeberCapResp); // T7b — the SAME token holds the jeeber cap
    }

    [Fact] // T7c — the contrast: client-only is denied the SAME jeeber route the dual token passed
    public async Task ClientOnlyToken_OnJeeberCap_Is403()
    {
        using var f = new WebApplicationFactory<Program>();
        var client = f.CreateClient().WithBearer(CapabilityTestHarness.MintBearer(f, "customer"));

        var resp = await client.PostAsync(OfferSubmitRoute, EmptyJson());

        await CapabilityTestHarness.AssertForbiddenCapabilityBody(resp);
    }

    [Theory] // UP-canon — both vocabularies yield identical dual-cap reach
    [InlineData("customer", "driver")]
    [InlineData("client", "jeeber")]
    public async Task DualRole_BothVocabularies_IdenticalOutcome(string r1, string r2)
    {
        using var f = new WebApplicationFactory<Program>();
        var bearer = CapabilityTestHarness.MintBearer(f, r1, r2);

        var clientCap = await f.CreateClient().WithBearer(bearer).PostAsync(RequestCreateRoute, EmptyJson());
        var jeeberCap = await f.CreateClient().WithBearer(bearer).PostAsync(OfferSubmitRoute, EmptyJson());

        AssertReachedController(clientCap);
        AssertReachedController(jeeberCap);
    }

    // ════════════════════════════════════════════════════════════════════════════════════════════
    // §7 — Edge X-User-Id / X-User-Roles path (L2-subject, not bypassed)
    // ════════════════════════════════════════════════════════════════════════════════════════════

    [Fact] // T5a
    public async Task EdgeAdminRole_OnAdminRoute_ReachesController()
    {
        using var f = new WebApplicationFactory<Program>();
        var client = f.CreateClient();
        client.DefaultRequestHeaders.Add("X-User-Id", CapabilityTestHarness.UserId);
        client.DefaultRequestHeaders.Add("X-User-Roles", "admin");

        var resp = await client.GetAsync(ZonesManageRoute);

        AssertReachedController(resp); // edge path authorizes admin caps
    }

    [Fact] // T5b
    public async Task EdgeClientRole_OnJeeberRoute_Is403()
    {
        using var f = new WebApplicationFactory<Program>();
        var client = f.CreateClient();
        client.DefaultRequestHeaders.Add("X-User-Id", CapabilityTestHarness.UserId);
        client.DefaultRequestHeaders.Add("X-User-Roles", "customer");

        var resp = await client.PostAsync(OfferSubmitRoute, EmptyJson());

        await CapabilityTestHarness.AssertForbiddenCapabilityBody(resp); // edge path is L2-subject
    }

    [Fact] // T5c
    public async Task EdgeNonAdminRoles_OnAdminRoute_Is403()
    {
        using var f = new WebApplicationFactory<Program>();
        var client = f.CreateClient();
        client.DefaultRequestHeaders.Add("X-User-Id", CapabilityTestHarness.UserId);
        client.DefaultRequestHeaders.Add("X-User-Roles", "driver,customer");

        var resp = await client.GetAsync(ZonesManageRoute);

        await CapabilityTestHarness.AssertForbiddenCapabilityBody(resp); // non-admin edge roles denied admin caps
    }
}
