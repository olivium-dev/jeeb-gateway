using System.Net;
using System.Text;
using FluentAssertions;
using JeebGateway.Services.Clients;
using JeebGateway.StateService;
using JeebGateway.StateService.Idempotency;
using JeebGateway.StateService.RateLimiting;
using JeebGateway.StateService.Durable;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Xunit;

namespace JeebGateway.IntegrationTests;

/// <summary>
/// Layer-2 durable rewire (R1, R8, R5) unit + middleware tests. These exercise
/// the gateway-side semantics against a hand-rolled fake of the NSwag client /
/// idempotency store so they run without a live jeeb-state-service.
/// </summary>
public class StateServiceRewireTests
{
    // ----------------------------------------------------------------------
    // R1 — Idempotency-Key middleware
    // ----------------------------------------------------------------------

    [Fact]
    public async Task Idempotency_Replay_Returns_Stored_Original_Without_Reinvoking()
    {
        var store = new FakeIdempotencyStore();
        var invocations = 0;
        using var server = await BuildIdempotencyServerAsync(store, ctx =>
        {
            invocations++;
            ctx.Response.StatusCode = StatusCodes.Status201Created;
            ctx.Response.ContentType = "application/json";
            return ctx.Response.WriteAsync($"{{\"orderId\":\"order-{invocations}\"}}");
        });

        var client = server.CreateClient();

        // First tap creates the order.
        var first = await PostWithKeyAsync(client, "/requests", "key-A", "{}");
        var firstBody = await first.Content.ReadAsStringAsync();

        // Second tap (same key) must replay the ORIGINAL, not create a new order.
        var second = await PostWithKeyAsync(client, "/requests", "key-A", "{}");
        var secondBody = await second.Content.ReadAsStringAsync();

        invocations.Should().Be(1, "the endpoint must run exactly once for a replayed key");
        first.StatusCode.Should().Be(HttpStatusCode.Created);
        second.StatusCode.Should().Be(HttpStatusCode.Created);
        secondBody.Should().Be(firstBody).And.Contain("order-1");
        second.Headers.Contains("Idempotency-Replayed").Should().BeTrue();
    }

    [Fact]
    public async Task Idempotency_No_Key_Passes_Through_Every_Time()
    {
        var store = new FakeIdempotencyStore();
        var invocations = 0;
        using var server = await BuildIdempotencyServerAsync(store, ctx =>
        {
            invocations++;
            ctx.Response.StatusCode = StatusCodes.Status200OK;
            return ctx.Response.WriteAsync("ok");
        });
        var client = server.CreateClient();

        await client.PostAsync("/requests", new StringContent("{}", Encoding.UTF8, "application/json"));
        await client.PostAsync("/requests", new StringContent("{}", Encoding.UTF8, "application/json"));

        invocations.Should().Be(2, "requests without an Idempotency-Key are never deduplicated");
        store.Records.Should().BeEmpty();
    }

    [Fact]
    public async Task Idempotency_Does_Not_Dedup_NonSuccess_Responses()
    {
        var store = new FakeIdempotencyStore();
        using var server = await BuildIdempotencyServerAsync(store, ctx =>
        {
            ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
            return ctx.Response.WriteAsync("bad");
        });
        var client = server.CreateClient();

        var resp = await PostWithKeyAsync(client, "/requests", "key-err", "{}");

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        store.Records.Should().BeEmpty("a failed attempt must remain retryable");
    }

    // ----------------------------------------------------------------------
    // DI graph — the NSwag client ctor is (string baseUrl, HttpClient); the
    // typed-client registration must supply the baseUrl or the gateway
    // crash-loops on first request (regression guard for the deploy that
    // rolled back with "Unable to resolve service for type 'System.String'").
    // ----------------------------------------------------------------------

    [Fact]
    public void StateServiceClient_Resolves_From_DI_When_Flag_On()
    {
        using var factory = new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program>()
            .WithWebHostBuilder(b =>
            {
                b.UseSetting("JeebStateService:BaseUrl", "http://127.0.0.1:10073");
                b.UseSetting("JeebStateService:Enabled", "true");
                b.ConfigureAppConfiguration((_, cfg) =>
                    cfg.AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        ["JeebStateService:BaseUrl"] = "http://127.0.0.1:10073",
                        ["JeebStateService:Enabled"] = "true"
                    }));
            });

        using var scope = factory.Services.CreateScope();
        var act = () => scope.ServiceProvider.GetRequiredService<IJeebStateServiceClient>();

        act.Should().NotThrow("the typed client must resolve with its baseUrl supplied");
        scope.ServiceProvider.GetRequiredService<IIdempotencyStore>().Should().NotBeNull();
        scope.ServiceProvider.GetRequiredService<IStateLockStore>().Should().NotBeNull();
    }

    // ----------------------------------------------------------------------
    // R8 — handover lock store (409 = held)
    // ----------------------------------------------------------------------

    [Fact]
    public async Task Lock_Acquire_Succeeds_When_Free()
    {
        var client = new FakeStateClient();
        var store = new StateServiceLockStore(client, NullLoggerFor<StateServiceLockStore>());

        var acquired = await store.TryAcquireAsync("handover:d1", "owner-1", 30, default);

        acquired.Should().BeTrue();
    }

    [Fact]
    public async Task Lock_Acquire_Returns_False_On_409_Held()
    {
        var client = new FakeStateClient { AcquireThrows = MakeApiException(409) };
        var store = new StateServiceLockStore(client, NullLoggerFor<StateServiceLockStore>());

        var acquired = await store.TryAcquireAsync("handover:d1", "owner-2", 30, default);

        acquired.Should().BeFalse("a lock held by another owner answers 409");
    }

    // ----------------------------------------------------------------------
    // R5 — dispute transition (409 = version conflict, race-safe)
    // ----------------------------------------------------------------------

    [Fact]
    public async Task Dispute_Transition_Returns_False_On_Version_Conflict()
    {
        var client = new FakeStateClient { TransitionThrows = MakeApiException(409) };
        var store = new StateServiceDisputeWriter(client, NullLoggerFor<StateServiceDisputeWriter>());

        var ok = await store.TransitionAsync(Guid.NewGuid(), "resolved", expectedVersion: 0,
            actor: "admin", eventType: "resolve", eventPayload: null, default);

        ok.Should().BeFalse("a stale expectedVersion loses the optimistic-concurrency race");
    }

    [Fact]
    public async Task Refresh_Rotate_Maps_409_To_ReuseDetected()
    {
        var client = new FakeStateClient { RotateThrows = MakeApiException(409) };
        var store = new StateServiceRefreshFamilyWriter(client, NullLoggerFor<StateServiceRefreshFamilyWriter>());

        var outcome = await store.RotateAsync("hash-A", "hash-B", DateTimeOffset.UtcNow.AddDays(1), default);

        outcome.Should().Be(RefreshRotateOutcome.ReuseDetectedFamilyRevoked);
    }

    // ----------------------------------------------------------------------
    // helpers
    // ----------------------------------------------------------------------

    private static async Task<TestServer> BuildIdempotencyServerAsync(
        IIdempotencyStore store, RequestDelegate endpoint)
    {
        var host = await new HostBuilder()
            .ConfigureWebHost(web =>
            {
                web.UseTestServer();
                web.ConfigureServices(services =>
                {
                    services.AddSingleton(store);
                    services.AddLogging();
                });
                web.Configure(app =>
                {
                    app.UseMiddleware<IdempotencyMiddleware>();
                    app.Run(endpoint);
                });
            })
            .StartAsync();
        return host.GetTestServer();
    }

    private static Task<HttpResponseMessage> PostWithKeyAsync(
        HttpClient client, string path, string key, string body)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };
        req.Headers.Add("Idempotency-Key", key);
        return client.SendAsync(req);
    }

    private static JeebStateServiceApiException MakeApiException(int status) =>
        new("state-service error", status, null, new Dictionary<string, IEnumerable<string>>(), null);

    private static ILogger<T> NullLoggerFor<T>() =>
        Microsoft.Extensions.Logging.Abstractions.NullLogger<T>.Instance;
}

/// <summary>In-memory fake of the gateway's R1 idempotency seam.</summary>
internal sealed class FakeIdempotencyStore : IIdempotencyStore
{
    public readonly Dictionary<string, IdempotencyOutcome> Records = new();

    public Task<IdempotencyOutcome> PutOrGetAsync(
        string key, int statusCode, string responseBodyJson, int ttlSeconds, CancellationToken ct)
    {
        if (Records.TryGetValue(key, out var existing))
        {
            return Task.FromResult(new IdempotencyOutcome
            {
                Inserted = false,
                StatusCode = existing.StatusCode,
                ResponseBodyJson = existing.ResponseBodyJson
            });
        }
        var outcome = new IdempotencyOutcome
        {
            Inserted = true,
            StatusCode = statusCode,
            ResponseBodyJson = responseBodyJson
        };
        Records[key] = outcome;
        return Task.FromResult(outcome);
    }

    public Task<IdempotencyOutcome?> GetAsync(string key, CancellationToken ct) =>
        Task.FromResult(Records.TryGetValue(key, out var v)
            ? new IdempotencyOutcome { Inserted = false, StatusCode = v.StatusCode, ResponseBodyJson = v.ResponseBodyJson }
            : null);

    public Task<System.Collections.Generic.IReadOnlyList<IdempotencyOutcome>> FindByPrefixAsync(
        string prefix, CancellationToken ct)
    {
        System.Collections.Generic.IReadOnlyList<IdempotencyOutcome> rows = System.Linq.Enumerable.ToList(
            System.Linq.Enumerable.Select(
                System.Linq.Enumerable.Where(Records, kvp => kvp.Key.StartsWith(prefix, System.StringComparison.Ordinal)),
                kvp => new IdempotencyOutcome
                {
                    Inserted = false,
                    StatusCode = kvp.Value.StatusCode,
                    ResponseBodyJson = kvp.Value.ResponseBodyJson,
                }));
        return Task.FromResult(rows);
    }
}

/// <summary>
/// Hand-rolled fake of the NSwag client; each op can be told to throw a
/// <see cref="JeebStateServiceApiException"/> with a chosen status code to
/// exercise the gateway's status-code → outcome mapping.
/// </summary>
internal sealed class FakeStateClient : IJeebStateServiceClient
{
    public JeebStateServiceApiException? AcquireThrows { get; set; }
    public JeebStateServiceApiException? RotateThrows { get; set; }
    public JeebStateServiceApiException? TransitionThrows { get; set; }

    public Task AcquireLockAsync(LockAcquireRequest body) => AcquireLockAsync(body, default);
    public Task AcquireLockAsync(LockAcquireRequest body, CancellationToken ct) =>
        AcquireThrows is null ? Task.CompletedTask : throw AcquireThrows;

    public Task ReleaseLockAsync(LockAcquireRequest body) => ReleaseLockAsync(body, default);
    public Task ReleaseLockAsync(LockAcquireRequest body, CancellationToken ct) => Task.CompletedTask;

    public Task RotateRefreshTokenAsync(RotateRequest body) => RotateRefreshTokenAsync(body, default);
    public Task RotateRefreshTokenAsync(RotateRequest body, CancellationToken ct) =>
        RotateThrows is null ? Task.CompletedTask : throw RotateThrows;

    public Task TransitionDisputeAsync(Guid caseId, DisputeTransitionRequest body) => TransitionDisputeAsync(caseId, body, default);
    public Task TransitionDisputeAsync(Guid caseId, DisputeTransitionRequest body, CancellationToken ct) =>
        TransitionThrows is null ? Task.CompletedTask : throw TransitionThrows;

    // Remaining members are unused by these tests.
    public Task UpsertIdempotencyKeyAsync(IdempotencyPutRequest body) => Task.CompletedTask;
    public Task UpsertIdempotencyKeyAsync(IdempotencyPutRequest body, CancellationToken ct) => Task.CompletedTask;
    public Task<IdempotencyRecord> GetIdempotencyKeyAsync(string key) => GetIdempotencyKeyAsync(key, default);
    public Task<IdempotencyRecord> GetIdempotencyKeyAsync(string key, CancellationToken ct) =>
        Task.FromResult(new IdempotencyRecord());
    public Task<System.Collections.Generic.IReadOnlyList<IdempotencyRecord>> FindIdempotencyKeysByPrefixAsync(
        string prefix, CancellationToken ct) =>
        Task.FromResult<System.Collections.Generic.IReadOnlyList<IdempotencyRecord>>(
            System.Array.Empty<IdempotencyRecord>());
    public Task CreateRefreshFamilyAsync(FamilyCreateRequest body) => Task.CompletedTask;
    public Task CreateRefreshFamilyAsync(FamilyCreateRequest body, CancellationToken ct) => Task.CompletedTask;
    public Task IsRefreshFamilyRevokedAsync(Guid familyId) => Task.CompletedTask;
    public Task IsRefreshFamilyRevokedAsync(Guid familyId, CancellationToken ct) => Task.CompletedTask;
    public Task CreateKycAsync(KycCreateRequest body) => Task.CompletedTask;
    public Task CreateKycAsync(KycCreateRequest body, CancellationToken ct) => Task.CompletedTask;
    public Task<KycRecord> GetKycAsync(Guid id) => Task.FromResult(new KycRecord());
    public Task<KycRecord> GetKycAsync(Guid id, CancellationToken ct) => Task.FromResult(new KycRecord());
    public Task<KycRecord> UpdateKycAsync(Guid id, KycUpdateRequest body) => Task.FromResult(new KycRecord());
    public Task<KycRecord> UpdateKycAsync(Guid id, KycUpdateRequest body, CancellationToken ct) => Task.FromResult(new KycRecord());
    public Task SubmitRatingAsync(RatingSubmitRequest body) => Task.CompletedTask;
    public Task SubmitRatingAsync(RatingSubmitRequest body, CancellationToken ct) => Task.CompletedTask;
    public Task RevealRatingsAsync(string contextId) => Task.CompletedTask;
    public Task RevealRatingsAsync(string contextId, CancellationToken ct) => Task.CompletedTask;
    public Task OpenDisputeAsync(DisputeOpenRequest body) => Task.CompletedTask;
    public Task OpenDisputeAsync(DisputeOpenRequest body, CancellationToken ct) => Task.CompletedTask;
    public Task AddStrikeAsync(StrikeAddRequest body) => Task.CompletedTask;
    public Task AddStrikeAsync(StrikeAddRequest body, CancellationToken ct) => Task.CompletedTask;
    public Task BumpCancellationCounterAsync(CancellationBumpRequest body) => Task.CompletedTask;
    public Task BumpCancellationCounterAsync(CancellationBumpRequest body, CancellationToken ct) => Task.CompletedTask;
    public Task EscalateOtpAsync(OtpEscalateRequest body) => Task.CompletedTask;
    public Task EscalateOtpAsync(OtpEscalateRequest body, CancellationToken ct) => Task.CompletedTask;
    public Task HitRateLimitAsync(RateLimitHitRequest body) => Task.CompletedTask;
    public Task HitRateLimitAsync(RateLimitHitRequest body, CancellationToken ct) => Task.CompletedTask;
}
