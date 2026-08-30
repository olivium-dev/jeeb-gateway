using System.Net;
using System.Net.Http.Json;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using JeebGateway.JeebWallet;
using JeebGateway.Partner.Auth;
using JeebGateway.service.ServiceUserManagement;
using JeebGateway.Tokens;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Xunit;

namespace JeebGateway.IntegrationTests;

/// <summary>
/// Tests for the ADDITIVE, ENV-GATED developer endpoints under <c>/dev/*</c>
/// (<see cref="JeebGateway.Controllers.DevController"/>), per
/// <c>SEED-SESSIONS-CONTRACT.md §1</c>.
///
/// Two contracts are asserted:
///   * <b>flag-off → 404</b> on EVERY dev route (the
///     <see cref="JeebGateway.Security.DevOnlyAttribute"/> gate). This is the
///     production-safety guarantee — the routes are indistinguishable from
///     "no such endpoint" while <c>Features:DevEndpoints:Enabled</c> is false
///     (which is the committed value in every environment).
///   * <b>flag-on</b> → <c>POST /dev/seed/user</c> calls the existing typed
///     <see cref="ServiceUserManagementClient"/> with the mapped
///     <see cref="RegisterUserRequest"/> and returns the upstream
///     <c>userId</c>; the inspect routes proxy the same client.
///
/// The UM client is replaced with one whose <see cref="HttpClient"/> is backed
/// by a stub handler (the same pattern as <c>UserPreferencesEndpointTests</c>),
/// so no live user-management is required.
/// </summary>
public class DevEndpointsTests
{
    private const string TestSigningKey = "jeeb-devtool-wallet-tests-signing-key-32bytes";
    // -----------------------------------------------------------------
    // flag OFF -> 404 on every dev route (production-safety guarantee)
    // -----------------------------------------------------------------

    [Theory]
    [InlineData("POST", "/dev/seed/user")]
    [InlineData("GET", "/dev/data/users")]
    [InlineData("GET", "/dev/data/users?runId=7f3a1c")]
    [InlineData("GET", "/dev/data/user/abc-123")]
    [InlineData("POST", "/dev/partner/credentials")]
    [InlineData("DELETE", "/dev/partner/credentials/demo-partner")]
    [InlineData("PUT", "/dev/wallets/jeeber/aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa/ensure")]
    public async Task DevRoutes_FlagOff_Return404(string method, string path)
    {
        // No stub needed: the gate short-circuits before any upstream call.
        using var factory = NewFactory(enabled: false, upstreamHandler: ThrowingHandler());
        var client = factory.CreateClient();

        var req = new HttpRequestMessage(new HttpMethod(method), path);
        if (method == "POST")
        {
            req.Content = JsonBody("""
                { "role": "client", "phone": "+96139120001", "displayName": "Sami" }
                """);
        }

        var resp = await client.SendAsync(req);

        resp.StatusCode.Should().Be(HttpStatusCode.NotFound,
            "every /dev/* route must behave as if it does not exist while the flag is off");
    }

    // -----------------------------------------------------------------
    // flag ON -> POST /dev/seed/user maps to UM RegisterUserRequest and
    // returns the upstream userId.
    // -----------------------------------------------------------------

    [Fact]
    public async Task SeedUser_FlagOn_CallsUserManagement_WithMappedRequest_AndReturnsUserId()
    {
        var captured = new CapturedRequests();
        var stub = new StubHttpMessageHandler(req =>
        {
            captured.Add(req, req.Content is null ? "" : req.Content.ReadAsStringAsync().GetAwaiter().GetResult());
            // UM RegisterUserResponse — the canonical id the gateway echoes.
            return JsonResponse("""
                {
                  "userId": "f1c2-real-um-id",
                  "username": "sami_run7f3a",
                  "email": "seed-7f3a-sami@jeeb.test",
                  "status": "created",
                  "createdDate": "2026-06-05T09:00:00Z"
                }
                """);
        });

        using var factory = NewFactory(enabled: true, upstreamHandler: stub);
        var client = factory.CreateClient();

        var resp = await client.PostAsync("/dev/seed/user", JsonBody("""
            {
              "role": "Client",
              "phone": "+96139120001",
              "displayName": "Sami (run 7f3a)",
              "runId": "7f3a1c",
              "tags": ["S02", "sami"]
            }
            """));

        resp.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await resp.Content.ReadFromJsonAsync<SeedUserResponseDto>();
        body.Should().NotBeNull();
        body!.UserId.Should().Be("f1c2-real-um-id");
        body.Role.Should().Be("client", "role is normalized to lowercase");
        body.Phone.Should().Be("+96139120001", "phone is echoed");
        body.RunId.Should().Be("7f3a1c");
        body.Tags.Should().Contain("S02").And.Contain("sami");
        body.Status.Should().Be("created");

        // The upstream UM register endpoint was hit exactly once with a POST.
        var sent = captured.Single();
        sent.Method.Should().Be(HttpMethod.Post);
        sent.RequestUri!.AbsolutePath.Should().EndWith("/api/User/register");

        // The mapped RegisterUserRequest carries a derived username/email and a
        // password == confirmPassword (the gateway generated a strong random pw),
        // and NEVER reflects the raw phone as a UM field name we did not map.
        var json = captured.LastBody;
        json.Should().Contain("\"email\":");
        json.Should().Contain("\"username\":");
        json.Should().Contain("\"password\":");
        json.Should().Contain("\"confirmPassword\":");
        // referralCode is a NON-NULL column in user-management; the seed must
        // always send it (even when the caller omits it) or UM rejects the insert.
        json.Should().Contain("\"referralCode\":",
            "user-management requires a non-null referralCode; the seed must always send it");
        json.Should().NotContain("\"phone\"", "UM has no phone field; the gateway must not invent one");
        json.Should().NotContain("\"role\"", "UM has no role field; role is seed metadata only");
    }

    /// <summary>
    /// Regression for the seed-400 defect: when the caller does NOT supply a
    /// referralCode, the gateway must still send a present, non-null
    /// <c>referralCode</c> (empty string) so the upstream user-management insert
    /// succeeds. Proves a REAL successful create end-to-end: upstream is hit once
    /// with a body that carries referralCode, and the gateway returns 200 with a
    /// non-empty userId — not merely the flag-off 404 case.
    /// </summary>
    [Fact]
    public async Task SeedUser_FlagOn_NoReferralCodeSupplied_StillSendsReferralCode_AndReturns200WithUserId()
    {
        var captured = new CapturedRequests();
        var stub = new StubHttpMessageHandler(req =>
        {
            captured.Add(req, req.Content is null ? "" : req.Content.ReadAsStringAsync().GetAwaiter().GetResult());
            return JsonResponse("""
                {
                  "userId": "real-created-id-001",
                  "username": "sami",
                  "email": "seed-sami@jeeb.test",
                  "status": "created",
                  "createdDate": "2026-06-05T09:00:00Z"
                }
                """);
        });

        using var factory = NewFactory(enabled: true, upstreamHandler: stub);
        var client = factory.CreateClient();

        // NOTE: no referralCode in the body — this is the persona path that 400'd.
        var resp = await client.PostAsync("/dev/seed/user", JsonBody("""
            { "role": "client", "phone": "+96139120001", "displayName": "Sami" }
            """));

        // The user-facing action SUCCEEDS: 200 + a non-empty canonical userId.
        resp.StatusCode.Should().Be(HttpStatusCode.OK,
            "with referralCode supplied the upstream insert succeeds and the seed returns 200");
        var body = await resp.Content.ReadFromJsonAsync<SeedUserResponseDto>();
        body.Should().NotBeNull();
        body!.UserId.Should().Be("real-created-id-001");
        body.UserId.Should().NotBeNullOrWhiteSpace("a real create must return a canonical userId");

        // The wire payload to UM carries a non-null referralCode (empty string).
        var json = captured.LastBody;
        json.Should().Contain("\"referralCode\":\"\"",
            "an omitted referralCode is sent as an empty string so the NOT NULL column is satisfied");
    }

    /// <summary>
    /// When the caller DOES supply a referralCode, the gateway forwards it
    /// verbatim (trimmed) to user-management and the create succeeds.
    /// </summary>
    [Fact]
    public async Task SeedUser_FlagOn_ReferralCodeSupplied_ForwardsIt_AndReturns200WithUserId()
    {
        var captured = new CapturedRequests();
        var stub = new StubHttpMessageHandler(req =>
        {
            captured.Add(req, req.Content is null ? "" : req.Content.ReadAsStringAsync().GetAwaiter().GetResult());
            return JsonResponse("""
                { "userId": "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa", "username": "jad", "email": "seed-jad@jeeb.test", "status": "created" }
                """);
        });

        using var factory = NewFactory(enabled: true, upstreamHandler: stub);
        var client = factory.CreateClient();

        var resp = await client.PostAsync("/dev/seed/user", JsonBody("""
            { "role": "jeeber", "phone": "+96139120009", "displayName": "Jad", "referralCode": "REF123" }
            """));

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<SeedUserResponseDto>();
        body!.UserId.Should().Be("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
        var json = captured.LastBody;
        json.Should().Contain("\"referralCode\":\"REF123\"",
            "a caller-supplied referralCode is forwarded verbatim to user-management");
    }

    [Fact]
    public async Task JeeberWalletEnsure_FailureThenRetry_IsRecoverableAndIdempotent()
    {
        var holderId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
        var wallets = new RecordingWalletProvisioner
        {
            Failure = new WalletProvisioningUnavailableException("down"),
        };
        using var factory = NewFactory(enabled: true, upstreamHandler: ThrowingHandler(), wallets);
        var client = factory.CreateClient();

        var first = await client.PutAsync($"/dev/wallets/jeeber/{holderId:D}/ensure", null);
        first.StatusCode.Should().Be(HttpStatusCode.BadGateway);

        wallets.Failure = null;
        var retry = await client.PutAsync($"/dev/wallets/jeeber/{holderId:D}/ensure", null);
        retry.StatusCode.Should().Be(HttpStatusCode.NoContent);
        wallets.JeeberIds.Should().Equal(holderId, holderId);
    }

    [Fact]
    public async Task SeedUser_FlagOn_NeverReturnsPassword()
    {
        var stub = new StubHttpMessageHandler(_ => JsonResponse("""
            { "userId": "bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb", "username": "u1", "email": "seed-x@jeeb.test", "status": "created" }
            """));

        using var factory = NewFactory(enabled: true, upstreamHandler: stub);
        var client = factory.CreateClient();

        var resp = await client.PostAsync("/dev/seed/user", JsonBody("""
            { "role": "jeeber", "phone": "+96139120002", "displayName": "Lina" }
            """));

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var raw = await resp.Content.ReadAsStringAsync();
        raw.ToLowerInvariant().Should().NotContain("password",
            "the dev seed response must never carry a password");
    }

    [Fact]
    public async Task SeedUser_FlagOn_MissingRequiredFields_Returns400()
    {
        using var factory = NewFactory(enabled: true, upstreamHandler: ThrowingHandler());
        var client = factory.CreateClient();

        // Missing displayName.
        var resp = await client.PostAsync("/dev/seed/user", JsonBody("""
            { "role": "client", "phone": "+96139120003" }
            """));

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task SeedUser_FlagOn_UpstreamConflict_IsSurfaced()
    {
        var stub = new StubHttpMessageHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.Conflict)
            {
                Content = new StringContent("username taken", Encoding.UTF8, "text/plain"),
            });

        using var factory = NewFactory(enabled: true, upstreamHandler: stub);
        var client = factory.CreateClient();

        var resp = await client.PostAsync("/dev/seed/user", JsonBody("""
            { "role": "client", "phone": "+96139120004", "displayName": "Dup" }
            """));

        // The gateway surfaces the upstream 4xx (not a 200).
        ((int)resp.StatusCode).Should().BeGreaterThanOrEqualTo(400);
        resp.StatusCode.Should().NotBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task PartnerCredential_ProvisionsWalletBeforeCredentialBecomesUsable()
    {
        var events = new List<string>();
        var wallets = new RecordingWalletProvisioner(events);
        var credentials = new RecordingCredentialStore(events);
        using var factory = NewFactory(
            enabled: true,
            upstreamHandler: ThrowingHandler(),
            wallets,
            credentials);
        var client = factory.CreateClient();

        var seed = await client.PostAsync("/dev/partner/credentials", JsonBody("""
            {
              "identifier": "devtool-partner-cccccccccccccccccccccccccccccccc",
              "holderId": "cccccccc-cccc-cccc-cccc-cccccccccccc",
              "displayName": "Demo Partner",
              "password": "runtime-only"
            }
            """));
        seed.StatusCode.Should().Be(HttpStatusCode.NoContent);

        var login = await client.PostAsync("/v1/partner/auth/login", JsonBody("""
            { "identifier": "devtool-partner-cccccccccccccccccccccccccccccccc", "password": "runtime-only" }
            """));
        login.StatusCode.Should().Be(HttpStatusCode.OK);
        using var loginJson = JsonDocument.Parse(await login.Content.ReadAsStringAsync());
        var accessToken = loginJson.RootElement.GetProperty("accessToken").GetString();
        var refreshToken = loginJson.RootElement.GetProperty("refreshToken").GetString();
        var secondLogin = await client.PostAsync("/v1/partner/auth/login", JsonBody("""
            { "identifier": "devtool-partner-cccccccccccccccccccccccccccccccc", "password": "runtime-only" }
            """));
        secondLogin.StatusCode.Should().Be(HttpStatusCode.Unauthorized,
            "runtime Dev Tool credentials are one-shot even before cleanup");
        events.Should().Equal(
            "credential-preflight", "partner-wallet", "credential", "verify", "verify");
        wallets.PartnerCalls.Should().ContainSingle()
            .Which.Should().Be((Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc"), "Demo Partner"));

        var remove = await client.DeleteAsync(
            "/dev/partner/credentials/devtool-partner-cccccccccccccccccccccccccccccccc"
            + "?holderId=cccccccc-cccc-cccc-cccc-cccccccccccc");
        remove.StatusCode.Should().Be(HttpStatusCode.NoContent);
        var afterRemove = await client.PostAsync("/v1/partner/auth/login", JsonBody("""
            { "identifier": "devtool-partner-cccccccccccccccccccccccccccccccc", "password": "runtime-only" }
            """));
        afterRemove.StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken);
        var revokedAccess = await client.GetAsync("/v1/partner/wallet");
        revokedAccess.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        client.DefaultRequestHeaders.Authorization = null;

        var revokedRefresh = await client.PostAsync("/auth/refresh", JsonBody($$"""
            { "refreshToken": "{{refreshToken}}" }
            """));
        revokedRefresh.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task PartnerCredential_WalletFailureDoesNotExposeLogin()
    {
        var events = new List<string>();
        var wallets = new RecordingWalletProvisioner(events)
        {
            Failure = new WalletProvisioningUnavailableException("down"),
        };
        var credentials = new RecordingCredentialStore(events);
        using var factory = NewFactory(
            enabled: true,
            upstreamHandler: ThrowingHandler(),
            wallets,
            credentials);
        var client = factory.CreateClient();

        var result = await client.PostAsync("/dev/partner/credentials", JsonBody("""
            {
              "identifier": "devtool-partner-dddddddddddddddddddddddddddddddd",
              "holderId": "dddddddd-dddd-dddd-dddd-dddddddddddd",
              "displayName": "Demo Partner",
              "password": "runtime-only"
            }
            """));
        result.StatusCode.Should().Be(HttpStatusCode.BadGateway);
        var login = await client.PostAsync("/v1/partner/auth/login", JsonBody("""
            { "identifier": "devtool-partner-dddddddddddddddddddddddddddddddd", "password": "runtime-only" }
            """));
        login.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        events.Should().Equal("credential-preflight", "partner-wallet", "verify");
        credentials.SeedCalls.Should().Be(0);
    }

    [Fact]
    public async Task PartnerCredential_CollisionIsRejectedBeforeWalletMutation()
    {
        var events = new List<string>();
        var wallets = new RecordingWalletProvisioner(events);
        var credentials = new RecordingCredentialStore(events)
        {
            PreflightFailure = new InvalidOperationException("configured collision"),
        };
        using var factory = NewFactory(
            enabled: true,
            upstreamHandler: ThrowingHandler(),
            wallets,
            credentials);
        var client = factory.CreateClient();

        var result = await client.PostAsync("/dev/partner/credentials", JsonBody("""
            {
              "identifier": "devtool-partner-aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
              "holderId": "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa",
              "displayName": "Must Not Mutate Wallet",
              "password": "runtime-only"
            }
            """));

        result.StatusCode.Should().Be(HttpStatusCode.Conflict);
        events.Should().Equal("credential-preflight");
        wallets.PartnerCalls.Should().BeEmpty();
        credentials.SeedCalls.Should().Be(0);
    }

    [Fact]
    public async Task PartnerCredential_CleanupRequiresHolderForColdReplicaRevocation()
    {
        using var factory = NewFactory(enabled: true, upstreamHandler: ThrowingHandler());
        var client = factory.CreateClient();

        var response = await client.DeleteAsync(
            "/dev/partner/credentials/devtool-partner-aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        problem.GetProperty("errors").GetProperty("holderId").GetArrayLength()
            .Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task PartnerCredential_LoginCleanupRaceReturnsGenericProblemDetails401()
    {
        var events = new List<string>();
        var credentials = new RecordingCredentialStore(events);
        var holderId = Guid.Parse("acacacac-acac-acac-acac-acacacacacac");
        var login = PartnerCredentialStore.RuntimeIdentifier(holderId);
        await credentials.ReserveRuntimeSeedAsync(
            login, holderId, "Race Demo", "runtime-only", CancellationToken.None);
        await credentials.ActivateRuntimeSeedAsync(login, holderId, CancellationToken.None);
        using var factory = NewFactory(
            enabled: true,
            upstreamHandler: ThrowingHandler(),
            credentials: credentials,
            tokens: new RevokedDuringBoundedIssueTokenService());
        var client = factory.CreateClient();

        var response = await client.PostAsync("/v1/partner/auth/login", JsonBody($$"""
            { "identifier": "{{login}}", "password": "runtime-only" }
            """));

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/problem+json");
        (await response.Content.ReadAsStringAsync())
            .Should().Contain("invalid-partner-credentials");
    }

    [Fact]
    public async Task PartnerCredential_OneShotDeadlineAndRevocationAreSharedAcrossColdReplicas()
    {
        var shared = new JeebGateway.StateService.Idempotency.InMemoryIdempotencyStore(TimeProvider.System);
        PartnerCredentialStore Store() => new(
            Options.Create(new PartnerAuthOptions()), shared, TimeProvider.System,
            NullLogger<PartnerCredentialStore>.Instance);
        var storeA = Store();
        var storeB = Store();
        var holderId = Guid.Parse("abababab-abab-abab-abab-abababababab");
        var login = PartnerCredentialStore.RuntimeIdentifier(holderId);
        await storeA.ReserveRuntimeSeedAsync(
            login, holderId, "Lease Demo", "same-secret", CancellationToken.None);
        await storeA.ActivateRuntimeSeedAsync(login, holderId, CancellationToken.None);
        (await storeB.VerifyAsync(login, "same-secret", CancellationToken.None))
            .Should().NotBeNull();

        await storeB.ReserveRuntimeSeedAsync(
            login, holderId, "Lease Demo", "same-secret", CancellationToken.None);
        (await storeA.VerifyAsync(login, "same-secret", CancellationToken.None))
            .Should().BeNull("an exact retry on another replica must not reset one-shot consumption");

        (await storeB.RemoveAsync(login, holderId, CancellationToken.None)).Should().Be(holderId);
        (await storeA.VerifyAsync(login, "same-secret", CancellationToken.None))
            .Should().BeNull("the shared revocation marker is visible to a cold replica");
    }

    [Fact]
    public async Task PartnerCredential_RealStoreRemovalRevokesIssuedSession()
    {
        using var factory = NewFactory(
            enabled: true,
            upstreamHandler: ThrowingHandler(),
            wallets: new RecordingWalletProvisioner());
        var client = factory.CreateClient();

        var seed = await client.PostAsync("/dev/partner/credentials", JsonBody("""
            {
              "identifier": "devtool-partner-eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee",
              "holderId": "eeeeeeee-eeee-eeee-eeee-eeeeeeeeeeee",
              "displayName": "Real Store Demo Partner",
              "password": "runtime-only"
            }
            """));
        seed.StatusCode.Should().Be(HttpStatusCode.NoContent);

        var login = await client.PostAsync("/v1/partner/auth/login", JsonBody("""
            { "identifier": "devtool-partner-eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee", "password": "runtime-only" }
            """));
        login.StatusCode.Should().Be(HttpStatusCode.OK);
        using var loginJson = JsonDocument.Parse(await login.Content.ReadAsStringAsync());
        var accessToken = loginJson.RootElement.GetProperty("accessToken").GetString();
        var refreshToken = loginJson.RootElement.GetProperty("refreshToken").GetString();
        var accessExpiry = loginJson.RootElement.GetProperty("accessTokenExpiresAt")
            .GetDateTimeOffset();
        var refreshExpiry = loginJson.RootElement.GetProperty("refreshTokenExpiresAt")
            .GetDateTimeOffset();
        accessExpiry.Should().BeOnOrBefore(
            DateTimeOffset.UtcNow.Add(PartnerCredentialStore.RuntimeCredentialLifetime));
        refreshExpiry.Should().BeOnOrBefore(
            DateTimeOffset.UtcNow.Add(PartnerCredentialStore.RuntimeCredentialLifetime));

        var refresh = await client.PostAsync("/auth/refresh", JsonBody($$"""
            { "refreshToken": "{{refreshToken}}" }
            """));
        refresh.StatusCode.Should().Be(HttpStatusCode.OK);
        using var refreshJson = JsonDocument.Parse(await refresh.Content.ReadAsStringAsync());
        accessToken = refreshJson.RootElement.GetProperty("accessToken").GetString();
        refreshToken = refreshJson.RootElement.GetProperty("refreshToken").GetString();
        refreshJson.RootElement.GetProperty("accessTokenExpiresAt").GetDateTimeOffset()
            .Should().BeOnOrBefore(refreshExpiry,
                "refresh rotation must preserve the original runtime-session deadline");
        refreshJson.RootElement.GetProperty("refreshTokenExpiresAt").GetDateTimeOffset()
            .Should().BeOnOrBefore(refreshExpiry,
                "refresh rotation must not extend the five-minute runtime session");
        var runtimeRefreshHash = new JwtSecurityTokenHandler()
            .ReadJwtToken(accessToken)
            .Claims.Single(claim =>
                claim.Type == JeebGateway.Tokens.TokenService.RuntimeRefreshHashClaim)
            .Value;

        client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue(
                "Bearer",
                RuntimeBearer(
                    Guid.Parse("eeeeeeee-eeee-eeee-eeee-eeeeeeeeeeee"),
                    DateTimeOffset.UtcNow.AddMinutes(1),
                    runtimeRefreshHash));
        var futureDeadline = await client.GetAsync("/v1/partner/wallet");
        futureDeadline.StatusCode.Should().NotBe(HttpStatusCode.Unauthorized,
            "the probe token must otherwise be accepted by the gateway JWT scheme");

        client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue(
                "Bearer",
                RuntimeBearer(
                    Guid.Parse("eeeeeeee-eeee-eeee-eeee-eeeeeeeeeeee"),
                    DateTimeOffset.UtcNow.AddSeconds(-1),
                    runtimeRefreshHash));
        var expiredDeadline = await client.GetAsync("/v1/partner/wallet");
        expiredDeadline.StatusCode.Should().Be(HttpStatusCode.Unauthorized,
            "the signed runtime deadline must remain strict even if in-memory state is absent");
        client.DefaultRequestHeaders.Authorization = null;

        var secondLogin = await client.PostAsync("/v1/partner/auth/login", JsonBody("""
            { "identifier": "devtool-partner-eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee", "password": "runtime-only" }
            """));
        secondLogin.StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        var remove = await client.DeleteAsync(
            "/dev/partner/credentials/devtool-partner-eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee"
            + "?holderId=eeeeeeee-eeee-eeee-eeee-eeeeeeeeeeee");
        remove.StatusCode.Should().Be(HttpStatusCode.NoContent);

        client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken);
        var revokedAccess = await client.GetAsync("/v1/partner/wallet");
        revokedAccess.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        client.DefaultRequestHeaders.Authorization = null;

        var revokedRefresh = await client.PostAsync("/auth/refresh", JsonBody($$"""
            { "refreshToken": "{{refreshToken}}" }
            """));
        revokedRefresh.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task PartnerCredential_ZeroHolderId_Returns400BeforeWalletMutation()
    {
        var wallets = new RecordingWalletProvisioner();
        using var factory = NewFactory(enabled: true, upstreamHandler: ThrowingHandler(), wallets);
        var client = factory.CreateClient();

        var result = await client.PostAsync("/dev/partner/credentials", JsonBody("""
            {
              "identifier": "demo-partner",
              "holderId": "00000000-0000-0000-0000-000000000000",
              "displayName": "Demo Partner",
              "password": "runtime-only"
            }
            """));

        result.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        wallets.PartnerCalls.Should().BeEmpty();
    }

    // -----------------------------------------------------------------
    // flag ON -> GET /dev/data/users proxies AllAsync and shapes the view
    // -----------------------------------------------------------------

    [Fact]
    public async Task GetUsers_FlagOn_ProxiesUserManagement_AndShapesView()
    {
        var captured = new CapturedRequests();
        var stub = new StubHttpMessageHandler(req =>
        {
            captured.Add(req, req.Content is null ? "" : req.Content.ReadAsStringAsync().GetAwaiter().GetResult());
            return JsonResponse("""
                {
                  "users": [
                    { "userId": "id-1", "username": "sami_run7f3a", "email": "seed-7f3a-sami@jeeb.test", "createdDate": "2026-06-05T09:00:00Z" }
                  ],
                  "totalCount": 1, "skip": 0, "limit": 50, "hasMore": false
                }
                """);
        });

        using var factory = NewFactory(enabled: true, upstreamHandler: stub);
        var client = factory.CreateClient();

        var resp = await client.GetAsync("/dev/data/users?runId=7f3a");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<UsersResponseDto>();
        body.Should().NotBeNull();
        body!.Source.Should().Be("user-management");
        body.RunIdFilter.Should().Be("7f3a");
        body.Count.Should().Be(1);
        body.Users.Should().ContainSingle(u => u.UserId == "id-1");

        captured.Single().Method.Should().Be(HttpMethod.Get);
        captured.Single().RequestUri!.AbsolutePath.Should().EndWith("/api/User/all");
    }

    [Fact]
    public async Task GetUsers_FlagOn_RunIdFilter_ExcludesNonMatching()
    {
        var stub = new StubHttpMessageHandler(_ => JsonResponse("""
            {
              "users": [
                { "userId": "id-1", "username": "sami_run7f3a", "email": "seed-7f3a-sami@jeeb.test" },
                { "userId": "id-2", "username": "other_runZZZZ", "email": "seed-zzzz-other@jeeb.test" }
              ],
              "totalCount": 2, "skip": 0, "limit": 50, "hasMore": false
            }
            """));

        using var factory = NewFactory(enabled: true, upstreamHandler: stub);
        var client = factory.CreateClient();

        var resp = await client.GetAsync("/dev/data/users?runId=7f3a");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<UsersResponseDto>();
        body!.Count.Should().Be(1, "only the user whose handle/email carries the run tag matches");
        body.Users.Single().UserId.Should().Be("id-1");
    }

    // -----------------------------------------------------------------
    // flag ON -> GET /dev/data/user/{id} proxies ProfileAsync
    // -----------------------------------------------------------------

    [Fact]
    public async Task GetUser_FlagOn_ProxiesProfile_AndShapesView()
    {
        var captured = new CapturedRequests();
        var stub = new StubHttpMessageHandler(req =>
        {
            captured.Add(req, req.Content is null ? "" : req.Content.ReadAsStringAsync().GetAwaiter().GetResult());
            return JsonResponse("""
                { "userId": "id-1", "username": "sami_run7f3a", "email": "seed-7f3a-sami@jeeb.test", "createdDate": "2026-06-05T09:00:00Z" }
                """);
        });

        using var factory = NewFactory(enabled: true, upstreamHandler: stub);
        var client = factory.CreateClient();

        var resp = await client.GetAsync("/dev/data/user/id-1");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<DevUserViewDto>();
        body!.UserId.Should().Be("id-1");
        body.Username.Should().Be("sami_run7f3a");

        captured.Single().Method.Should().Be(HttpMethod.Get);
        captured.Single().RequestUri!.AbsolutePath.Should().Contain("/api/User/");
    }

    // -----------------------------------------------------------------
    // helpers
    // -----------------------------------------------------------------

    private static WebApplicationFactory<Program> NewFactory(
        bool enabled,
        HttpMessageHandler upstreamHandler,
        RecordingWalletProvisioner? wallets = null,
        IPartnerCredentialStore? credentials = null,
        ITokenService? tokens = null)
    {
        return new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.UseSetting("Features:DevEndpoints:Enabled", enabled ? "true" : "false");
                builder.UseSetting("Jwt:SigningKey", TestSigningKey);

                builder.ConfigureTestServices(services =>
                {
                    // Replace the scoped UM client with one whose HttpClient is
                    // backed by the stub handler.
                    services.RemoveAll<ServiceUserManagementClient>();
                    services.RemoveAll<IJeeberWalletProvisioner>();
                    services.RemoveAll<IPartnerWalletProvisioner>();
                    if (credentials is not null)
                    {
                        services.RemoveAll<IPartnerCredentialStore>();
                        services.AddSingleton(credentials);
                    }
                    if (tokens is not null)
                    {
                        services.RemoveAll<ITokenService>();
                        services.AddSingleton(tokens);
                    }
                    var walletStub = wallets ?? new RecordingWalletProvisioner();
                    services.AddSingleton<IJeeberWalletProvisioner>(walletStub);
                    services.AddSingleton<IPartnerWalletProvisioner>(walletStub);
                    services.AddScoped(_ =>
                    {
                        var http = new HttpClient(upstreamHandler)
                        {
                            BaseAddress = new Uri("http://um.test/"),
                        };
                        return new ServiceUserManagementClient("http://um.test/", http);
                    });
                });
            });
    }

    private static StringContent JsonBody(string json)
        => new(json, Encoding.UTF8, "application/json");

    private static string RuntimeBearer(
        Guid holderId,
        DateTimeOffset runtimeDeadline,
        string runtimeRefreshHash)
    {
        var now = DateTimeOffset.UtcNow;
        var credentials = new SigningCredentials(
            new SymmetricSecurityKey(Encoding.UTF8.GetBytes(TestSigningKey)),
            SecurityAlgorithms.HmacSha256);
        var token = new JwtSecurityToken(
            issuer: "jeeb-gateway",
            audience: "jeeb-clients",
            claims:
            [
                new Claim(JwtRegisteredClaimNames.Sub, holderId.ToString()),
                new Claim("roles", "partner"),
                new Claim("active_role", "partner"),
                new Claim(
                    JeebGateway.Tokens.TokenService.RuntimeSessionExpiryClaim,
                    runtimeDeadline.ToUnixTimeSeconds().ToString(),
                    ClaimValueTypes.Integer64),
                new Claim(
                    JeebGateway.Tokens.TokenService.RuntimeRefreshHashClaim,
                    runtimeRefreshHash),
            ],
            notBefore: now.AddSeconds(-5).UtcDateTime,
            expires: now.AddMinutes(5).UtcDateTime,
            signingCredentials: credentials);
        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    private static HttpResponseMessage JsonResponse(string json, HttpStatusCode status = HttpStatusCode.OK)
        => new(status)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };

    /// <summary>A handler that fails the test if any upstream call is made.</summary>
    private static StubHttpMessageHandler ThrowingHandler()
        => new(_ => throw new InvalidOperationException(
            "upstream user-management must NOT be called when the dev flag is off or the request is invalid"));

    private sealed class CapturedRequests
    {
        private readonly List<HttpRequestMessage> _items = new();
        private readonly List<string> _bodies = new();
        public void Add(HttpRequestMessage req, string body)
        {
            _items.Add(req);
            _bodies.Add(body);
        }
        public HttpRequestMessage Single() => _items.Single();
        public string LastBody => _bodies[^1];
    }

    private sealed class StubHttpMessageHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _handler;
        public StubHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> handler) => _handler = handler;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(_handler(request));
    }

    private sealed class RecordingWalletProvisioner :
        IJeeberWalletProvisioner,
        IPartnerWalletProvisioner
    {
        private readonly List<string>? _events;

        public RecordingWalletProvisioner(List<string>? events = null) => _events = events;

        public Exception? Failure { get; set; }
        public List<Guid> JeeberIds { get; } = new();
        public List<(Guid HolderId, string HolderName)> PartnerCalls { get; } = new();

        public Task EnsureAsync(Guid holderId, CancellationToken ct)
        {
            _events?.Add("jeeber-wallet");
            JeeberIds.Add(holderId);
            return Failure is null ? Task.CompletedTask : Task.FromException(Failure);
        }

        public Task EnsureAsync(Guid holderId, string holderName, CancellationToken ct)
        {
            _events?.Add("partner-wallet");
            PartnerCalls.Add((holderId, holderName));
            return Failure is null ? Task.CompletedTask : Task.FromException(Failure);
        }
    }

    private sealed class RecordingCredentialStore(List<string> events) : IPartnerCredentialStore
    {
        private readonly Dictionary<string, PartnerAccount> _accounts =
            new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, string> _secrets =
            new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _consumed = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, (Guid HolderId, string DisplayName, string Secret)> _pending =
            new(StringComparer.OrdinalIgnoreCase);
        public int SeedCalls { get; private set; }
        public Exception? PreflightFailure { get; init; }

        public Task<PartnerAccount?> VerifyAsync(
            string login,
            string secret,
            CancellationToken ct)
        {
            events.Add("verify");
            var account =
                !_consumed.Contains(login)
                && _secrets.TryGetValue(login, out var expected)
                && expected == secret
                && _accounts.TryGetValue(login, out var candidate)
                    ? candidate
                    : null;
            if (account is not null) _consumed.Add(login);
            return Task.FromResult(account);
        }

        public Task ReserveRuntimeSeedAsync(
            string login, Guid holderId, string displayName, string secret, CancellationToken ct)
        {
            events.Add("credential-preflight");
            if (PreflightFailure is not null) throw PreflightFailure;
            _pending[login] = (holderId, displayName, secret);
            return Task.CompletedTask;
        }

        public Task ActivateRuntimeSeedAsync(string login, Guid holderId, CancellationToken ct)
        {
            events.Add("credential");
            SeedCalls++;
            var pending = _pending[login];
            _secrets[login] = pending.Secret;
            _accounts[login] = new PartnerAccount(
                holderId, login, pending.DisplayName, DateTimeOffset.UtcNow.AddMinutes(5));
            return Task.CompletedTask;
        }

        public Task<Guid> RemoveAsync(string login, Guid expectedHolderId, CancellationToken ct)
        {
            _secrets.Remove(login);
            if (_accounts.Remove(login, out var account))
            {
                _revokedHolders.Add(account.HolderId);
                return Task.FromResult(account.HolderId);
            }
            _revokedHolders.Add(expectedHolderId);
            return Task.FromResult(expectedHolderId);
        }

        private readonly HashSet<Guid> _revokedHolders = new();

    }

    private sealed class RevokedDuringBoundedIssueTokenService : ITokenService
    {
        public Task<TokenPair> IssueAsync(
            string userId,
            IEnumerable<string> roles,
            CancellationToken ct) => throw new NotSupportedException();

        public Task<TokenPair> IssueBoundedAsync(
            string userId,
            IEnumerable<string> roles,
            string activeRole,
            DateTimeOffset absoluteSessionExpiresAt,
            CancellationToken ct) => throw new InvalidOperationException(
                "cleanup won the durable bounded-session race");

        public Task<RefreshResult> RefreshAsync(
            string refreshToken,
            CancellationToken ct) => throw new NotSupportedException();

        public Task RevokeAsync(
            string refreshToken,
            RevocationReason reason,
            CancellationToken ct) => throw new NotSupportedException();

        public Task<int> RevokeAllForUserAsync(
            string userId,
            RevocationReason reason,
            CancellationToken ct) => throw new NotSupportedException();
    }

    // --- response DTOs (test-local; mirror DevController response shapes) ---

    private sealed class SeedUserResponseDto
    {
        public string? UserId { get; set; }
        public string? Role { get; set; }
        public string? Phone { get; set; }
        public string? DisplayName { get; set; }
        public string? Username { get; set; }
        public string? Email { get; set; }
        public string? Status { get; set; }
        public string? RunId { get; set; }
        public string[]? Tags { get; set; }
    }

    private sealed class UsersResponseDto
    {
        public List<DevUserViewDto> Users { get; set; } = new();
        public int Count { get; set; }
        public string? Source { get; set; }
        public string? RunIdFilter { get; set; }
    }

    private sealed class DevUserViewDto
    {
        public string? UserId { get; set; }
        public string? Username { get; set; }
        public string? Email { get; set; }
        public string? Status { get; set; }
    }
}
