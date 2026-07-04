using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using JeebGateway.Disputes.V2;
using JeebGateway.Services.Clients;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace JeebGateway.IntegrationTests.Disputes;

/// <summary>
/// JEBV4-44 (PP-5) — the dispute → UPG refund branch (real money OUT) had
/// ZERO test coverage before this ticket. Pins down DisputeCaseService.cs:275-308:
/// <list type="bullet">
///   <item>Successful resolve-with-refund calls the refund client EXACTLY
///     ONCE with idempotency key <c>dispute:{caseId}:refund</c>, and the
///     durable resolution write happens AFTER the refund succeeds.</item>
///   <item>A refund failure aborts the state transition entirely — the case
///     stays open with <see cref="ResolveOutcome.RefundFailed"/> and NO
///     durable resolution write is persisted.</item>
///   <item>An admin retry after "refund succeeded but the durable write
///     failed" re-sends the SAME idempotency key (UPG would dedupe); the
///     retry completes and no second refund amount is recorded.</item>
/// </list>
/// All three run against <see cref="InMemoryPaymentRefundClient"/> / a thin
/// ordering-observing decorator over it — never a real
/// <c>unified_payment_gateway</c> call, matching the existing fake-client
/// pattern used by <c>DisputeCaseEndpointTests</c>.
/// </summary>
public class DisputeRefundOrderingTests
{
    // ------------------------------------------------------------------
    // AC1 — refund is called exactly once with the documented idempotency
    // key, and the refund happens BEFORE the durable resolution write
    // (DisputeCaseService.cs:275-308: refund first, ApplyResolutionAsync
    // second). Proven by having the refund client itself query the case
    // store mid-call and observe the case is still 'open'.
    // ------------------------------------------------------------------
    [Fact]
    public async Task Resolve_With_Refund_Calls_Refund_Once_Before_The_Durable_Write()
    {
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll(typeof(IPaymentRefundClient));
                services.AddSingleton<OrderingObservingRefundClient>();
                services.AddSingleton<IPaymentRefundClient>(sp => sp.GetRequiredService<OrderingObservingRefundClient>());
            });
        });

        const string client = "c-ord-1";
        const string jeeber = "j-ord-1";
        var deliveryId = await SeedDeliveryWithJeeberAsync(factory, client, jeeber);

        var http = ClientFor(factory, client);
        var fileResp = await http.PostAsJsonAsync($"/v1/deliveries/{deliveryId}/escalate", new EscalateDeliveryRequest
        {
            Reason = "overcharged"
        });
        fileResp.EnsureSuccessStatusCode();
        var @case = await fileResp.Content.ReadFromJsonAsync<DisputeCaseResponse>();

        var admin = AdminClientFor(factory, "admin-ord-1");
        var resolveResp = await admin.PostAsJsonAsync($"/admin/v1/disputes/{@case!.Id}/resolve", new ResolveCaseRequest
        {
            Decision = "refund",
            RefundUsd = 9.50m,
            Notes = "approved"
        });
        resolveResp.StatusCode.Should().Be(HttpStatusCode.OK);

        var refundClient = factory.Services.GetRequiredService<OrderingObservingRefundClient>();

        refundClient.Calls.Should().HaveCount(1, "the refund must be attempted exactly once");
        refundClient.Calls[0].IdempotencyKey.Should().Be($"dispute:{@case.Id}:refund",
            "the idempotency key must be derived deterministically from the case id");
        refundClient.Calls[0].AmountUsd.Should().Be(9.50m);

        refundClient.CaseStateObservedDuringCall.Should().Be(DisputeCaseState.Open,
            "the durable resolution write (state → resolved_refund) must NOT have happened yet when " +
            "the refund call is made — refund-before-write ordering per DisputeCaseService.cs:275-308");

        var resolved = await resolveResp.Content.ReadFromJsonAsync<DisputeCaseResponse>();
        resolved!.State.Should().Be(DisputeCaseState.ResolvedRefund,
            "after a successful refund the durable write DOES land, moving the case to its terminal state");
    }

    // ------------------------------------------------------------------
    // AC2 — refund failure aborts the resolution: case stays open with
    // RefundFailed, and NO durable write happens (state never advances,
    // ResolverAdminId / ResolvedAt never stamped).
    // ------------------------------------------------------------------
    [Fact]
    public async Task Resolve_With_Refund_Failure_Aborts_Resolution_And_Persists_No_Write()
    {
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll(typeof(IPaymentRefundClient));
                services.AddSingleton<IPaymentRefundClient, AlwaysFailingRefundClient>();
            });
        });

        const string client = "c-ord-2";
        const string jeeber = "j-ord-2";
        var deliveryId = await SeedDeliveryWithJeeberAsync(factory, client, jeeber);

        var http = ClientFor(factory, client);
        var fileResp = await http.PostAsJsonAsync($"/v1/deliveries/{deliveryId}/escalate", new EscalateDeliveryRequest
        {
            Reason = "overcharged"
        });
        fileResp.EnsureSuccessStatusCode();
        var @case = await fileResp.Content.ReadFromJsonAsync<DisputeCaseResponse>();

        var admin = AdminClientFor(factory, "admin-ord-2");
        var resolveResp = await admin.PostAsJsonAsync($"/admin/v1/disputes/{@case!.Id}/resolve", new ResolveCaseRequest
        {
            Decision = "refund",
            RefundUsd = 25m,
            Notes = "should abort"
        });

        // PO blocker #4: the failed-refund path is surfaced, not a bare 200.
        // The controller maps ResolveOutcome.RefundFailed — assert on the
        // case's post-attempt state instead of pinning an exact status code
        // the controller mapping owns.
        resolveResp.IsSuccessStatusCode.Should().BeFalse(
            "a refund failure must not report success back to the admin");

        var store = factory.Services.GetRequiredService<IDisputeCaseStore>();
        var afterAttempt = await store.GetByIdAsync(@case!.Id, CancellationToken.None);

        afterAttempt.Should().NotBeNull();
        afterAttempt!.State.Should().Be(DisputeCaseState.Open,
            "on refund failure the case must remain open — no half-resolved case");
        afterAttempt.ResolverAdminId.Should().BeNull("no durable resolution write may land on refund failure");
        afterAttempt.ResolvedAt.Should().BeNull("no durable resolution write may land on refund failure");
        afterAttempt.RefundLedgerEntryId.Should().BeNull("a failed refund must not record a ledger entry id");
    }

    // ------------------------------------------------------------------
    // AC3 — admin retries after "refund succeeded but the durable write
    // failed": the SAME idempotency key is re-sent (UPG would dedupe), the
    // retry completes, and no second refund amount is recorded. Simulated
    // via a decorator over InMemoryDisputeCaseStore that throws on the
    // FIRST ApplyResolutionAsync call per case (modelling a transient write
    // failure) and succeeds on every subsequent call.
    // ------------------------------------------------------------------
    [Fact]
    public async Task Retry_After_Write_Failure_Reuses_Idempotency_Key_And_Does_Not_Double_Refund()
    {
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll(typeof(IPaymentRefundClient));
                services.AddSingleton<IPaymentRefundClient, InMemoryPaymentRefundClient>();

                // Decorate whatever IDisputeCaseStore is already registered
                // (InMemoryDisputeCaseStore by default) so ApplyResolutionAsync
                // fails exactly once per case id.
                services.RemoveAll(typeof(IDisputeCaseStore));
                services.AddSingleton<IDisputeCaseStore>(_ =>
                    new WriteOnceFlakyDisputeCaseStore(new InMemoryDisputeCaseStore()));
            });
        });

        const string client = "c-ord-3";
        const string jeeber = "j-ord-3";
        var deliveryId = await SeedDeliveryWithJeeberAsync(factory, client, jeeber);

        var http = ClientFor(factory, client);
        var fileResp = await http.PostAsJsonAsync($"/v1/deliveries/{deliveryId}/escalate", new EscalateDeliveryRequest
        {
            Reason = "overcharged"
        });
        fileResp.EnsureSuccessStatusCode();
        var @case = await fileResp.Content.ReadFromJsonAsync<DisputeCaseResponse>();

        var admin = AdminClientFor(factory, "admin-ord-3");
        admin.DefaultRequestHeaders.Add("Idempotency-Key", "admin-retry-key-1");

        // First attempt: refund succeeds, but the durable write throws
        // (WriteOnceFlakyDisputeCaseStore) — the gateway's global exception
        // handler maps this to a non-2xx; the case must still be 'open'.
        var first = await admin.PostAsJsonAsync($"/admin/v1/disputes/{@case!.Id}/resolve", new ResolveCaseRequest
        {
            Decision = "refund",
            RefundUsd = 15m,
            Notes = "first attempt — write will fail"
        });
        first.IsSuccessStatusCode.Should().BeFalse("the durable write is simulated to fail on the first attempt");

        var storeAfterFirst = factory.Services.GetRequiredService<IDisputeCaseStore>();
        (await storeAfterFirst.GetByIdAsync(@case.Id, CancellationToken.None))!.State
            .Should().Be(DisputeCaseState.Open, "the failed write must not have landed");

        // Retry with the SAME admin Idempotency-Key header. DisputeCaseService
        // re-derives the SAME dispute:{caseId}:refund key regardless of the
        // HTTP-level Idempotency-Key, so UPG (here, InMemoryPaymentRefundClient)
        // dedupes the refund itself.
        var retry = await admin.PostAsJsonAsync($"/admin/v1/disputes/{@case.Id}/resolve", new ResolveCaseRequest
        {
            Decision = "refund",
            RefundUsd = 15m,
            Notes = "retry — write should succeed now"
        });
        retry.StatusCode.Should().Be(HttpStatusCode.OK, "the retry's durable write succeeds this time");
        var resolved = await retry.Content.ReadFromJsonAsync<DisputeCaseResponse>();
        resolved!.State.Should().Be(DisputeCaseState.ResolvedRefund);

        var refundClient = (InMemoryPaymentRefundClient)factory.Services.GetRequiredService<IPaymentRefundClient>();
        refundClient.Entries.Count(e => e.CaseId == @case.Id).Should().Be(1,
            "the refund must be recorded exactly once across both attempts — UPG's own idempotency-key " +
            "dedup on 'dispute:{caseId}:refund' must prevent a second refund amount on retry");
    }

    // ------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------

    private static HttpClient ClientFor(WebApplicationFactory<Program> factory, string userId, string role = JeebGateway.Users.Roles.Client)
    {
        var c = factory.CreateClient();
        c.DefaultRequestHeaders.Add("X-User-Id", userId);
        c.DefaultRequestHeaders.Add("X-User-Roles", role);
        return c;
    }

    private static HttpClient AdminClientFor(WebApplicationFactory<Program> factory, string adminId)
    {
        var c = factory.CreateClient();
        c.DefaultRequestHeaders.Add("X-User-Id", adminId);
        c.DefaultRequestHeaders.Add("X-User-Roles", JeebGateway.Users.Roles.Admin);
        return c;
    }

    private static async Task<string> SeedDeliveryWithJeeberAsync(
        WebApplicationFactory<Program> factory, string clientId, string jeeberId)
    {
        var store = factory.Services.GetRequiredService<JeebGateway.Requests.IRequestsStore>();
        var created = await store.CreateAsync(new JeebGateway.Requests.CreateRequestInput
        {
            ClientId = clientId,
            Description = "test delivery"
        }, CancellationToken.None);

        var lookup = await store.GetAsync(created.Id, CancellationToken.None);
        if (lookup is not null)
        {
            lookup.JeeberId = jeeberId;
        }
        return created.Id;
    }

    /// <summary>
    /// Wraps <see cref="InMemoryPaymentRefundClient"/>'s success behaviour but
    /// additionally captures, for each call, the calling case's state as read
    /// from <see cref="IDisputeCaseStore"/> AT THE MOMENT the refund call is
    /// made — proving the durable write has not happened yet (refund-before-write
    /// ordering).
    /// </summary>
    private sealed class OrderingObservingRefundClient : IPaymentRefundClient
    {
        private readonly IServiceProvider _services;
        private readonly List<RefundRequest> _calls = new();
        public IReadOnlyList<RefundRequest> Calls => _calls;
        public string? CaseStateObservedDuringCall { get; private set; }

        // Constructor-injected root IServiceProvider so RefundAsync can read
        // the CURRENT persisted case state (via IDisputeCaseStore) at the
        // exact moment the refund call happens — proving refund-before-write
        // ordering without needing a second, hand-rolled store fake.
        public OrderingObservingRefundClient(IServiceProvider services) => _services = services;

        public async Task<RefundResult> RefundAsync(RefundRequest request, CancellationToken ct)
        {
            _calls.Add(request);

            var store = _services.GetRequiredService<IDisputeCaseStore>();
            var @case = await store.GetByIdAsync(request.CaseId, ct);
            CaseStateObservedDuringCall = @case?.State;

            return new RefundResult
            {
                Success = true,
                LedgerEntryId = $"refund-{request.IdempotencyKey}"
            };
        }
    }

    private sealed class AlwaysFailingRefundClient : IPaymentRefundClient
    {
        public Task<RefundResult> RefundAsync(RefundRequest request, CancellationToken ct)
            => Task.FromResult(new RefundResult { Success = false, FailureReason = "upstream declined (test double)" });
    }

    /// <summary>
    /// Decorator over any <see cref="IDisputeCaseStore"/> that throws on the
    /// FIRST <see cref="ApplyResolutionAsync"/> call for a given case id
    /// (modelling a transient durable-write failure AFTER a refund has
    /// already succeeded) and delegates normally on every subsequent call for
    /// that same case.
    /// </summary>
    private sealed class WriteOnceFlakyDisputeCaseStore : IDisputeCaseStore
    {
        private readonly IDisputeCaseStore _inner;
        private readonly ConcurrentDictionary<string, bool> _failedOnce = new(StringComparer.Ordinal);

        public WriteOnceFlakyDisputeCaseStore(IDisputeCaseStore inner) => _inner = inner;

        public Task<DisputeCase> AddAsync(DisputeCase @case, CancellationToken ct) => _inner.AddAsync(@case, ct);

        public Task<DisputeCase?> GetByIdAsync(string caseId, CancellationToken ct) => _inner.GetByIdAsync(caseId, ct);

        public Task<DisputeCase?> GetByIdempotencyKeyAsync(string idempotencyKey, CancellationToken ct)
            => _inner.GetByIdempotencyKeyAsync(idempotencyKey, ct);

        public Task<DisputeCase?> GetActiveForDeliveryAsync(string deliveryId, CancellationToken ct)
            => _inner.GetActiveForDeliveryAsync(deliveryId, ct);

        public Task<IReadOnlyList<DisputeCase>> ListForUserAsync(string userId, CancellationToken ct)
            => _inner.ListForUserAsync(userId, ct);

        public Task<DisputeCase?> ApplyResolutionAsync(string caseId, DisputeCaseResolutionPatch patch, CancellationToken ct)
        {
            if (_failedOnce.TryAdd(caseId, true))
            {
                throw new InvalidOperationException(
                    $"simulated transient durable-write failure for case {caseId} (test double)");
            }
            return _inner.ApplyResolutionAsync(caseId, patch, ct);
        }

        public Task<DisputeCase?> ReplaceEvidenceAsync(string caseId, DisputeEvidence evidence, CancellationToken ct)
            => _inner.ReplaceEvidenceAsync(caseId, evidence, ct);

        public Task<DisputeCase?> ApplyUnderReviewAsync(string caseId, CancellationToken ct)
            => _inner.ApplyUnderReviewAsync(caseId, ct);
    }
}
