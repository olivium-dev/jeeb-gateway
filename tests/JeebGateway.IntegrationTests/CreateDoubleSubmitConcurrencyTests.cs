using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using JeebGateway.Availability;
using JeebGateway.Requests;
using JeebGateway.StateService.Idempotency;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace JeebGateway.IntegrationTests;

/// <summary>
/// JEBV4-45 (PP-8) — server-side create double-submit was unguarded AND
/// untested: two rapid identical <c>POST /requests</c> produce two orders,
/// two broadcasts — a money/trust event (the known defect M5#9, which only
/// ever got a CLIENT-side guard — B-02 / JEBV4-24 — so a retry-layer resend,
/// flaky network, or second device still hits the server unprotected).
///
/// <see cref="JeebGateway.Requests.InMemoryRequestsStore.TryCreateWithLimitAsync"/>
/// only enforces the BR-9 active-request cap under its process-local lock — it
/// has NO content/key-based dedup, so two concurrent creates from the same
/// client always land as two distinct rows unless something ahead of the
/// controller (the gateway-wide <see cref="IdempotencyMiddleware"/>) collapses
/// them onto one.
///
/// This suite fires GENUINELY concurrent (<see cref="Task.WhenAll"/>, not
/// sequential await) identical creates and asserts on the STORE'S row count —
/// not just the HTTP response — because the existing
/// <c>CreateIdempotencyReplayTests</c> only exercises SEQUENTIAL replay
/// (await first, then await second) and therefore cannot catch a race in the
/// middleware's check-then-execute window.
/// </summary>
public class CreateDoubleSubmitConcurrencyTests
{
    /// <summary>
    /// Mounts the gateway-wide <see cref="IdempotencyMiddleware"/> the same
    /// way <c>CreateIdempotencyReplayTests</c> does (state-service configured,
    /// in-process fake store) — this is the ONLY server-side dedup mechanism
    /// that exists today for create.
    /// </summary>
    private sealed class IdempotencyWiredFactory : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(Microsoft.AspNetCore.Hosting.IWebHostBuilder builder)
        {
            builder.UseSetting("JeebStateService:BaseUrl", "http://localhost:10073");
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll(typeof(IIdempotencyStore));
                services.AddSingleton<IIdempotencyStore, RaceObservingIdempotencyStore>();
            });
        }
    }

    // ------------------------------------------------------------------
    // Primary AC: two TRULY concurrent identical creates carrying the SAME
    // client-supplied Idempotency-Key (the realistic "client retried /
    // double-tapped with its own dedup key" shape) must result in exactly
    // ONE row in the authoritative request store — not just one HTTP
    // response shape.
    // ------------------------------------------------------------------
    [Fact]
    public async Task Two_Concurrent_Identical_Creates_With_Same_Key_Produce_Exactly_One_Order()
    {
        using var factory = new IdempotencyWiredFactory();
        var clientId = $"c-dbl-{Guid.NewGuid()}";
        var idempotencyKey = Guid.NewGuid().ToString();

        var httpA = ClientFor(factory, clientId);
        var httpB = ClientFor(factory, clientId);

        var taskA = PostAsync(httpA, idempotencyKey);
        var taskB = PostAsync(httpB, idempotencyKey);
        var responses = await Task.WhenAll(taskA, taskB);

        foreach (var resp in responses)
        {
            resp.StatusCode.Should().Be(HttpStatusCode.Created,
                "a double-submitted create must never surface a hard failure to either caller");
        }

        var ids = new List<string>();
        foreach (var resp in responses)
        {
            var dto = await resp.Content.ReadFromJsonAsync<CreatedDto>();
            ids.Add(dto!.Id);
        }

        ids.Distinct().Should().ContainSingle(
            "both concurrent callers must observe the SAME order id — the duplicate must collapse onto " +
            "the original, not mint a second id (JEBV4-45 duplicate-response semantics: same-id, not 409)");

        var store = factory.Services.GetRequiredService<IRequestsStore>();
        var rows = await store.ListForClientAsync(clientId, CancellationToken.None);
        rows.Should().ContainSingle(
            "[JEBV4-45] exactly ONE order must exist in the authoritative store after two concurrent " +
            "identical creates — if this fails, the IdempotencyMiddleware's check-then-execute window " +
            "(GetAsync existing-check happens BEFORE the handler runs and BEFORE PutOrGetAsync persists) " +
            "let both concurrent callers past the 'already exists?' gate and both independently wrote a " +
            "row to IRequestsStore, even though the HTTP responses were deduped. The fix is a per-key " +
            "critical section in IdempotencyMiddleware so the loser waits for the winner's write to land " +
            "instead of racing it — see IdempotencyMiddleware.cs.");
    }

    /// <summary>
    /// Sanity control: two concurrent creates with DIFFERENT keys are two
    /// distinct, legitimate orders — the dedup must not over-collapse.
    /// </summary>
    [Fact]
    public async Task Two_Concurrent_Creates_With_Different_Keys_Produce_Two_Orders()
    {
        using var factory = new IdempotencyWiredFactory();
        var clientId = $"c-dbl-distinct-{Guid.NewGuid()}";

        var httpA = ClientFor(factory, clientId);
        var httpB = ClientFor(factory, clientId);

        var responses = await Task.WhenAll(
            PostAsync(httpA, Guid.NewGuid().ToString()),
            PostAsync(httpB, Guid.NewGuid().ToString()));

        var store = factory.Services.GetRequiredService<IRequestsStore>();
        var rows = await store.ListForClientAsync(clientId, CancellationToken.None);
        rows.Should().HaveCount(2, "distinct Idempotency-Keys represent two genuinely different creates");
    }

    // ------------------------------------------------------------------
    // Guard AC: "the accept path is NOT this bug" — 2 concurrent accepts of
    // the SAME offer must resolve to exactly one winner (already believed
    // atomic via IRequestsStore.TryAcceptByJeeberAsync's process-local write
    // lock). This is a regression guard, not expected to fail.
    // ------------------------------------------------------------------
    [Fact]
    public async Task Two_Concurrent_Accepts_Of_One_Offer_Resolve_To_Exactly_One_Winner()
    {
        using var factory = new WebApplicationFactory<Program>();
        var jeeberId = $"j-race-{Guid.NewGuid()}";
        var clientId = $"c-race-{Guid.NewGuid()}";

        var requests = factory.Services.GetRequiredService<IRequestsStore>();
        var created = await requests.CreateAsync(new CreateRequestInput
        {
            ClientId = clientId,
            Description = "concurrent accept guard"
        }, CancellationToken.None);

        var offers = factory.Services.GetRequiredService<InMemoryPendingOffersStore>();
        var offer = offers.EnqueueForTest(jeeberId, created.Id);

        var clientA = ClientActor(factory, clientId);
        var clientB = ClientActor(factory, clientId);

        var responses = await Task.WhenAll(
            clientA.PostAsync($"/offers/{offer.Id}/accept", content: null),
            clientB.PostAsync($"/offers/{offer.Id}/accept", content: null));

        responses.Count(r => r.StatusCode == HttpStatusCode.OK).Should().Be(1,
            "exactly one of the two concurrent accept calls must win");
        responses.Count(r => r.StatusCode == HttpStatusCode.Conflict).Should().Be(1,
            "the losing concurrent accept call must observe a 409, not a silent second success");

        var finalRequest = await requests.GetAsync(created.Id, CancellationToken.None);
        finalRequest!.Status.Should().Be(RequestStatus.Accepted);
        finalRequest.JeeberId.Should().Be(jeeberId);
    }

    // ------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------

    private static HttpClient ClientFor(WebApplicationFactory<Program> factory, string userId)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-User-Id", userId);
        client.DefaultRequestHeaders.Add("X-User-Roles", "customer");
        return client;
    }

    private static HttpClient ClientActor(WebApplicationFactory<Program> factory, string clientId)
    {
        var c = factory.CreateClient();
        c.DefaultRequestHeaders.Add("X-User-Id", clientId);
        c.DefaultRequestHeaders.Add("X-User-Roles", "customer");
        return c;
    }

    private static Task<HttpResponseMessage> PostAsync(HttpClient client, string idempotencyKey)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, "/requests")
        {
            Content = JsonContent.Create(new
            {
                description = "double-submit guard payload",
                tierId = "flash",
                pickupLocation = new { lat = 33.88, lng = 35.50 },
                dropoffLocation = new { lat = 33.89, lng = 35.51 }
            })
        };
        req.Headers.Add("Idempotency-Key", idempotencyKey);
        return client.SendAsync(req);
    }

    private sealed record CreatedDto(string Id, string Status);

    /// <summary>
    /// Same atomic put-or-get semantics as
    /// <c>CreateIdempotencyReplayTests.IdempotencyFactory.InMemoryIdempotencyStore</c>
    /// (ON CONFLICT DO NOTHING via <see cref="ConcurrentDictionary{TKey,TValue}.GetOrAdd(TKey,TValue)"/>),
    /// reproduced here so this suite has no compile-time dependency on
    /// another test file's private nested type.
    /// </summary>
    private sealed class RaceObservingIdempotencyStore : IIdempotencyStore
    {
        private readonly ConcurrentDictionary<string, IdempotencyOutcome> _rows = new(StringComparer.Ordinal);

        public Task<IdempotencyOutcome> PutOrGetAsync(
            string key, int statusCode, string responseBodyJson, int ttlSeconds, CancellationToken ct)
        {
            var mine = new IdempotencyOutcome
            {
                Inserted = true,
                StatusCode = statusCode,
                ResponseBodyJson = responseBodyJson
            };
            var stored = _rows.GetOrAdd(key, mine);
            var inserted = ReferenceEquals(stored, mine);
            return Task.FromResult(new IdempotencyOutcome
            {
                Inserted = inserted,
                StatusCode = stored.StatusCode,
                ResponseBodyJson = stored.ResponseBodyJson
            });
        }

        public Task<IdempotencyOutcome?> GetAsync(string key, CancellationToken ct)
        {
            _rows.TryGetValue(key, out var row);
            return Task.FromResult(row is null
                ? null
                : new IdempotencyOutcome { Inserted = false, StatusCode = row.StatusCode, ResponseBodyJson = row.ResponseBodyJson });
        }

        public Task<IReadOnlyList<IdempotencyOutcome>> FindByPrefixAsync(string prefix, CancellationToken ct)
        {
            IReadOnlyList<IdempotencyOutcome> rows = _rows
                .Where(kvp => kvp.Key.StartsWith(prefix, StringComparison.Ordinal))
                .Select(kvp => new IdempotencyOutcome
                {
                    Inserted = false,
                    StatusCode = kvp.Value.StatusCode,
                    ResponseBodyJson = kvp.Value.ResponseBodyJson
                })
                .ToList();
            return Task.FromResult(rows);
        }
    }
}
