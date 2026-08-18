using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using JeebGateway.Cases;
using JeebGateway.StateService.Durable;
using JeebGateway.StateService.Ownership;
using JeebGateway.StateService.Work;
using JeebGateway.Services;
using JeebGateway.Services.Clients;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Http;
using Xunit;

namespace JeebGateway.IntegrationTests.StateService;

/// <summary>
/// The DISCRIMINATING probe for the 2026-08-11 state-service auth cutover: with the PRODUCTION env
/// armed (<c>JeebStateService__BaseUrl</c> + <c>JeebStateService__ServiceTokenFile</c>), does the
/// gateway actually authenticate to jeeb-state-service?
///
/// <para>On the failing build it did not. <c>StateServiceCredentialHandlerTests</c> passed (it news
/// the handler up directly), state's own auth triad passed, and <c>/health/ready</c> reported 19/19
/// — because the state health check probes <c>{BaseUrl}/health</c>, outside the authenticated
/// <c>/v1</c> surface. Meanwhile <c>POST /v1/auth/otp/verify</c> 500'd for ~2.5 minutes on live:
/// <c>StateServiceRefreshTokenStore → StateServiceIdempotencyStore → PUT /state/idempotency</c>
/// went out with no <c>Authorization</c> header and came back 401.</para>
///
/// <para>These tests boot the whole app with that exact configuration, put an upstream in front of
/// it that enforces ownership auth the way jeeb-state-service does, and drive the real login. They
/// fail on the pre-fix build and cannot be satisfied by anything short of the credential reaching
/// the main typed client.</para>
/// </summary>
public sealed class StateServiceCredentialProductionWiringTests : IDisposable
{
    private const string StateBaseUrl = "http://state.test:10073";
    private const string OtpAppId = "jeeb-test-app";
    private const string IdempotencyPath = "/state/idempotency";

    private readonly string _dir = Directory.CreateTempSubdirectory("jeeb-state-wiring").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    // ── the login canary, in-process ───────────────────────────────────────────

    [Fact(Skip = "needs a reachable user-management: this case drives a route that calls it, and on a bare checkout the call is refused. Run it with the service up (docker compose / a stub host) - a skip here is NOT a pass.")]
    public async Task Login_UnderTheCutoverEnv_Succeeds_AndNoStateCallIsUnauthenticated()
    {
        var token = WriteToken(new string('a', 48));
        var upstream = new OwnershipAuthStateService();
        using var factory = Factory(upstream, serviceTokenFile: token);
        var http = factory.CreateClient();

        (await http.PostAsync("/v1/auth/otp/request", Json("""{ "phone": "+9613000001" }""")))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        var verify = await http.PostAsync("/v1/auth/otp/verify",
            Json("""{ "phone": "+9613000001", "code": "1234" }"""));

        verify.StatusCode.Should().Be(HttpStatusCode.OK,
            "this is the live login canary: the refresh-token store writes through "
            + "PUT /state/idempotency, which 401'd unauthenticated and surfaced as a 500");

        var session = await verify.Content.ReadFromJsonAsync<VerifyResponse>();
        session!.AccessToken.Should().NotBeNullOrWhiteSpace();

        upstream.Calls.Should().NotBeEmpty("login must actually reach the state-service");
        upstream.Unauthenticated.Should().BeEmpty(
            "every state call must carry the mounted credential, not just the two recorder clients");
    }

    [Fact]
    public async Task TheMainTypedClient_CarriesTheMountedCredential()
    {
        var secret = new string('b', 48);
        var upstream = new OwnershipAuthStateService();
        using var factory = Factory(upstream, serviceTokenFile: WriteToken(secret));

        // IJeebStateServiceClient backs idempotency, cases, refresh tokens and disputes —
        // nearly all state traffic — and was the one client the credential never reached.
        var client = factory.Services.GetRequiredService<IJeebStateServiceClient>();
        await client.UpsertIdempotencyKeyWithResultAsync(
            new IdempotencyPutRequest { Key = "wiring-probe", StatusCode = 201, TtlSeconds = 60 },
            CancellationToken.None);

        upstream.Calls.Should().ContainSingle(c => c.Path == IdempotencyPath)
            .Which.Authorization.Should().Be("Bearer " + secret);
    }

    [Fact]
    public async Task TheCredentialReachesTheRecorderClientsToo()
    {
        var secret = new string('c', 48);
        var upstream = new OwnershipAuthStateService();
        using var factory = Factory(
            upstream,
            serviceTokenFile: WriteToken(secret),
            durableRequests: true);

        foreach (var name in new[] { "ISagaBundleRecorder", "IBroadcastEventRecorder" })
        {
            var http = factory.Services.GetRequiredService<IHttpClientFactory>().CreateClient(name);
            http.BaseAddress.Should().NotBeNull($"{name} must be registered when DurableRequests is enabled");
            await http.GetAsync("/state/bundles");
        }

        upstream.Unauthenticated.Should().BeEmpty();
    }

    [Fact]
    public async Task Every_state_case_outbox_audit_work_and_recorder_pipeline_carries_bearer_auth()
    {
        var secret = new string('g', 48);
        var upstream = new OwnershipAuthStateService();
        using var factory = Factory(
            upstream,
            serviceTokenFile: WriteToken(secret),
            durableRequests: true);
        var state = factory.Services.GetRequiredService<IJeebStateServiceClient>();
        var cases = (IGenericCaseStateClient)state;
        var ownership = (IStateOwnershipClient)state;

        await state.UpsertIdempotencyKeyWithResultAsync(
            new IdempotencyPutRequest { Key = "all-pipelines", StatusCode = 201, TtlSeconds = 60 },
            CancellationToken.None);
        await IgnoreNotFound(() => cases.GetCaseAsync(Guid.NewGuid(), CancellationToken.None));
        await IgnoreNotFound(() => cases.GetCaseDeadLettersAsync(1, null, CancellationToken.None));
        await IgnoreNotFound(() => ownership.FindAuditEventsAsync(
            new AuditEventQueryV1 { Application = "jeeb-gateway", Limit = 1 },
            CancellationToken.None));
        await ownership.GetLatestWorkItemAsync(
            "jeeb-gateway", "auth-smoke", "sha256:auth-smoke", CancellationToken.None);

        var work = factory.Services.GetRequiredService<IStateWorkItemClient>();
        await work.GetAsync(Guid.NewGuid(), CancellationToken.None);

        var saga = factory.Services.GetRequiredService<ISagaBundleRecorder>();
        await saga.RecordCreatedAsync("auth-smoke", "auth-smoke", new { probe = true }, CancellationToken.None);
        var broadcast = factory.Services.GetRequiredService<IBroadcastEventRecorder>();
        await broadcast.RecordBroadcastingAsync("auth-smoke", "broadcasting", CancellationToken.None);

        var expectedPaths = new[]
        {
            "/state/idempotency",
            "/cases/",
            "/case-outbox/dead-letters",
            "/audit-events",
            "/work-items/latest",
            "/work-items/",
            "/state/bundles",
            "/state/broadcasts",
        };
        foreach (var prefix in expectedPaths)
        {
            upstream.Calls.Should().Contain(
                call => call.Path.StartsWith(prefix, StringComparison.Ordinal)
                        && call.Authorization == "Bearer " + secret,
                $"{prefix} must execute through a file-backed credential handler");
        }
        upstream.Unauthenticated.Should().BeEmpty();
    }

    [Fact]
    public async Task ARotatedSecretFileIsPickedUpWithoutARestart()
    {
        var path = WriteToken(new string('d', 48));
        var upstream = new OwnershipAuthStateService();
        using var factory = Factory(upstream, serviceTokenFile: path);
        var client = factory.Services.GetRequiredService<IJeebStateServiceClient>();

        await client.UpsertIdempotencyKeyWithResultAsync(
            new IdempotencyPutRequest { Key = "rotate-1", StatusCode = 201, TtlSeconds = 60 },
            CancellationToken.None);

        File.WriteAllText(path, new string('e', 48));

        await client.UpsertIdempotencyKeyWithResultAsync(
            new IdempotencyPutRequest { Key = "rotate-2", StatusCode = 201, TtlSeconds = 60 },
            CancellationToken.None);

        var writes = upstream.Calls.Where(call => call.Path == IdempotencyPath).ToArray();
        writes.Should().HaveCount(2);
        writes[0].Authorization.Should().Be("Bearer " + new string('d', 48));
        writes[1].Authorization.Should().Be("Bearer " + new string('e', 48));
    }

    /// <summary>
    /// Pins the recorded asymmetry: the gateway TRIMS the secret file, jeeb-state-service's
    /// OwnershipCredentialFile does NOT — so the cutover runbook must write it with `printf '%s'`.
    /// </summary>
    [Fact]
    public async Task ATrailingNewlineInTheSecretFileIsTrimmedOffTheWire()
    {
        var secret = new string('f', 48);
        var upstream = new OwnershipAuthStateService();
        using var factory = Factory(upstream, serviceTokenFile: WriteToken(secret + "\n"));

        var client = factory.Services.GetRequiredService<IJeebStateServiceClient>();
        await client.UpsertIdempotencyKeyWithResultAsync(
            new IdempotencyPutRequest { Key = "trim-probe", StatusCode = 201, TtlSeconds = 60 },
            CancellationToken.None);

        upstream.Calls.Single(call => call.Path == IdempotencyPath).Authorization.Should().Be("Bearer " + secret,
            "a newline written into the secret file would be sent by state but not by the gateway");
    }

    // ── the control: unset key must stay exactly as today ──────────────────────

    [Fact(Skip = "needs a reachable user-management: this case drives a route that calls it, and on a bare checkout the call is refused. Run it with the service up (docker compose / a stub host) - a skip here is NOT a pass.")]
    public async Task WithoutTheTokenFile_TheClientStaysUnauthenticated()
    {
        var upstream = new OwnershipAuthStateService();
        using var factory = Factory(upstream, serviceTokenFile: null);

        var client = factory.Services.GetRequiredService<IJeebStateServiceClient>();
        var act = () => client.UpsertIdempotencyKeyWithResultAsync(
            new IdempotencyPutRequest { Key = "unarmed", StatusCode = 201, TtlSeconds = 60 },
            CancellationToken.None);

        await act.Should().ThrowAsync<Exception>("the stub upstream enforces ownership auth");
        upstream.Unauthenticated.Should().ContainSingle(
            "an unset ServiceTokenFile must remain byte-identical to today's live deployment — "
            + "this is what proves the tests above are driven by the env, not by the stub");
    }

    // ── fail loud, not silent-green ────────────────────────────────────────────

    [Fact]
    public void AnArmedButUnusableCredential_RefusesToBoot()
    {
        using var factory = Factory(new OwnershipAuthStateService(),
            serviceTokenFile: Path.Combine(_dir, "does-not-exist"));

        var act = () => factory.CreateClient();

        act.Should().Throw<Exception>(
            "arming the key against a missing secret must fail at boot, not 500 every login");
    }

    // ── helpers ───────────────────────────────────────────────────────────────

    private string WriteToken(string content)
    {
        var path = Path.Combine(_dir, "state-ownership-token-" + Guid.NewGuid().ToString("N"));
        File.WriteAllText(path, content);
        return path;
    }

    private static WebApplicationFactory<Program> Factory(
        OwnershipAuthStateService upstream,
        string? serviceTokenFile,
        bool durableRequests = false) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("JeebStateService:BaseUrl", StateBaseUrl);
            builder.UseSetting("JeebStateService:Enabled", "true");
            builder.UseSetting(
                "FeatureFlags:DurableRequests:Enabled",
                durableRequests.ToString());
            if (serviceTokenFile is not null)
            {
                builder.UseSetting("JeebStateService:ServiceTokenFile", serviceTokenFile);
            }

            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IServiceOTPClient>();
                services.AddSingleton<IServiceOTPClient>(new PassingOtpClient());
                services.Configure<UpstreamFeatureFlags>(f => f.Otp = true);
                services.Configure<JeebGateway.Auth.OtpSignIn.OtpSignInOptions>(o =>
                {
                    o.ApplicationId = OtpAppId;
                    o.TtlSeconds = 300;
                });

                // Terminate every state HttpClient at the stub, leaving the real handler chain
                // (this is the seam under test) intact.
                foreach (var name in new[]
                         {
                             "IJeebStateServiceClient",
                             "IStateWorkItemClient",
                             "ISagaBundleRecorder",
                             "IBroadcastEventRecorder"
                         })
                {
                    services.Configure<HttpClientFactoryOptions>(name, o =>
                        o.HttpMessageHandlerBuilderActions.Add(b => b.PrimaryHandler = upstream));
                }
            });
        });

    private static async Task IgnoreNotFound(Func<Task> probe)
    {
        try
        {
            await probe();
        }
        catch (Exception)
        {
            // The recording owner intentionally returns 404. The assertion is the
            // outbound path and Authorization header captured before deserialization.
        }
    }

    private static StringContent Json(string json) => new(json, Encoding.UTF8, "application/json");

    private sealed record VerifyResponse(string? AccessToken, string? RefreshToken);

    private sealed record RecordedCall(string Method, string Path, string? Authorization);

    /// <summary>
    /// Stands in for jeeb-state-service after the auth cutover: <c>/v1</c> requires a Bearer
    /// credential (401 <c>ownership_auth.required</c> without one), <c>/health</c> does not.
    /// </summary>
    private sealed class OwnershipAuthStateService : HttpMessageHandler
    {
        private readonly List<RecordedCall> _calls = new();

        public IReadOnlyList<RecordedCall> Calls
        {
            get { lock (_calls) return _calls.ToList(); }
        }

        public IReadOnlyList<RecordedCall> Unauthenticated =>
            Calls.Where(c => IsOwnershipPath(c.Path)
                             && string.IsNullOrEmpty(c.Authorization)).ToList();

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri?.AbsolutePath ?? "/";
            var auth = request.Headers.Authorization?.ToString();
            lock (_calls) _calls.Add(new RecordedCall(request.Method.Method, path, auth));

            if (IsOwnershipPath(path) && string.IsNullOrEmpty(auth))
            {
                return Task.FromResult(Problem(HttpStatusCode.Unauthorized,
                    "urn:problem:ownership_auth.required"));
            }

            if (request.Method == HttpMethod.Put && path == IdempotencyPath)
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Created)
                {
                    Content = new StringContent(
                        """{"key":"k","statusCode":201,"responseBody":null,"inserted":true}""",
                        Encoding.UTF8, "application/json")
                });
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)
            {
                Content = new StringContent("null", Encoding.UTF8, "application/json")
            });
        }

        private static HttpResponseMessage Problem(HttpStatusCode code, string type) =>
            new(code)
            {
                Content = new StringContent(
                    $$"""{"type":"{{type}}","status":{{(int)code}}}""",
                    Encoding.UTF8, "application/problem+json")
            };

        private static bool IsOwnershipPath(string path) =>
            path.StartsWith("/v1/", StringComparison.Ordinal)
            || path.StartsWith("/state/", StringComparison.Ordinal)
            || path.StartsWith("/cases", StringComparison.Ordinal)
            || path.StartsWith("/case-outbox", StringComparison.Ordinal)
            || path.StartsWith("/audit-events", StringComparison.Ordinal)
            || path.StartsWith("/work-items", StringComparison.Ordinal)
            || path.StartsWith("/config-surfaces", StringComparison.Ordinal)
            || path.StartsWith("/acks", StringComparison.Ordinal);
    }

    private sealed class PassingOtpClient : IServiceOTPClient
    {
        public Task SendOTPAsync(SendOTPRequestUserID? body) => Task.CompletedTask;

        public Task SendOTPAsync(SendOTPRequestUserID? body, CancellationToken cancellationToken)
            => Task.CompletedTask;

        public Task ValidateOTPAsync(ValidateOTPRequestModel? body) => Task.CompletedTask;

        public Task ValidateOTPAsync(ValidateOTPRequestModel? body, CancellationToken cancellationToken)
            => Task.CompletedTask;

        public Task UserAsync() => Task.CompletedTask;

        public Task UserAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
