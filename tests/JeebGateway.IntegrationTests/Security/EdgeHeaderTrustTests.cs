using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace JeebGateway.IntegrationTests.Security;

/// <summary>
/// SEC-C1 (Leg-11) — the gateway must NOT trust client-supplied X-User-Id / X-User-Roles
/// identity headers on the public edge. Identity may come only from a verified JWT, or from
/// an internal-only header proven to originate from a trusted edge via a shared secret.
///
/// These tests drive the /users/me/data-export identity surface (which resolves identity via
/// <see cref="JeebGateway.Users.UserIdentity"/> exactly like every other header-fallback
/// endpoint) under a production-like environment. A valid non-placeholder signing key is
/// injected so the SEC-H2 boot guard is satisfied and does not mask the C1 behaviour.
/// </summary>
public class EdgeHeaderTrustTests
{
    private const string ValidSigningKey = "edge-c1-test-signing-key-please-32-bytes-min";
    private const string EdgeSecret = "trusted-edge-shared-secret-value";
    private const string ForgedUserId = "99999999-aaaa-bbbb-cccc-dddddddddddd";

    private static WebApplicationFactory<Program> ProdFactory(string? edgeSecret)
        => new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            // Program reads Jwt:SigningKey eagerly at builder-configuration time, so it must be
            // supplied via UseSetting (host configuration, available before user code) rather than a
            // deferred ConfigureAppConfiguration source. A real (non-placeholder) key also keeps the
            // SEC-H2 boot guard from masking the C1 behaviour under test.
            b.UseEnvironment("Production");
            b.UseSetting("Security:RateLimit:Enabled", "false");
            b.UseSetting("Security:TokenMint:Enabled", "false");
            b.UseSetting("Jwt:SigningKey", ValidSigningKey);
            // Test-harness escape hatch: boot Production without real durable stores by disabling the
            // fail-closed StoreDurabilityGuard ONLY here (real prod never sets this → still fail-closed).
            b.UseSetting("StoreDurability:FailClosedDisabled", "true");
            b.UseSetting("Security:EdgeIdentity:SharedSecret", edgeSecret ?? string.Empty);
        });

    [Fact]
    public async Task Production_Forged_Identity_Headers_Are_Rejected()
    {
        using var factory = ProdFactory(edgeSecret: null); // fail closed: no trusted edge configured
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-User-Id", ForgedUserId);
        client.DefaultRequestHeaders.Add("X-User-Roles", "admin"); // attacker also self-assigns admin

        var resp = await client.GetAsync("/users/me/data-export");

        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized,
            "in production with no trusted-edge secret, raw client X-User-* headers must NOT authenticate");
    }

    [Fact]
    public async Task Production_Edge_Headers_With_Wrong_Secret_Are_Rejected()
    {
        using var factory = ProdFactory(edgeSecret: EdgeSecret);
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-User-Id", ForgedUserId);
        client.DefaultRequestHeaders.Add("X-User-Roles", "client");
        client.DefaultRequestHeaders.Add("X-Edge-Auth", "not-the-real-secret");

        var resp = await client.GetAsync("/users/me/data-export");

        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized,
            "a wrong edge secret must fail the constant-time comparison and leave the caller unidentified");
    }

    [Fact]
    public async Task Production_Edge_Headers_With_Correct_Secret_Resolve_Identity()
    {
        using var factory = ProdFactory(edgeSecret: EdgeSecret);
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-User-Id", ForgedUserId);
        client.DefaultRequestHeaders.Add("X-User-Roles", "client");
        client.DefaultRequestHeaders.Add("X-Edge-Auth", EdgeSecret);

        var resp = await client.GetAsync("/users/me/data-export");

        // Identity now resolves (the trusted edge presented the secret), so the request is
        // authorized; there is simply no export record yet → 404, and crucially NOT 401/403.
        resp.StatusCode.Should().Be(HttpStatusCode.NotFound,
            "a request from the trusted edge (correct secret) must resolve identity and be authorized");
    }

    [Fact]
    public async Task Development_Header_Identity_Still_Works()
    {
        // Guardrail: the fix must NOT break the local-dev / test-harness header identity path.
        using var factory = new WebApplicationFactory<Program>(); // default env = Development
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-User-Id", "dev-c1-user");
        client.DefaultRequestHeaders.Add("X-User-Roles", "client");

        var resp = await client.GetAsync("/users/me/data-export");

        resp.StatusCode.Should().Be(HttpStatusCode.NotFound,
            "in Development the X-User-* header identity remains trusted (unchanged behaviour)");
    }

    [Fact]
    public void EdgeIdentityTrust_FailsClosed_When_No_Environment_Service()
    {
        // A bare context with no IHostEnvironment / SecurityOptions in RequestServices must
        // default to NOT trusting inbound identity headers.
        var ctx = new DefaultHttpContext();
        ctx.Request.Headers["X-User-Id"] = "someone";

        JeebGateway.Security.EdgeIdentityTrust.HeadersTrusted(ctx).Should().BeFalse();
        JeebGateway.Security.EdgeIdentityTrust.HeadersTrusted(null).Should().BeFalse();
    }
}
