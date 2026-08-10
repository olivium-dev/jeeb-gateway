using System.Collections.Concurrent;
using FluentAssertions;
using JeebGateway.StateService.Durable;
using JeebGateway.StateService.Idempotency;
using Xunit;

namespace JeebGateway.IntegrationTests;

public sealed class StateServiceOfferRequestIndexTests
{
    [Fact]
    public void Record_and_resolve_use_only_external_owner_state()
    {
        var owner = new FakeExternalIdempotencyStore();
        var index = new StateServiceOfferRequestIndex(owner);

        index.Record("offer-1", "request-1", "jeeber-1");

        index.ResolveRequestId("offer-1").Should().Be("request-1");
        index.ResolveJeeberId("offer-1").Should().Be("jeeber-1");
        index.ListOfferIdsForJeeber("jeeber-1").Should().Equal("offer-1");
    }

    [Fact]
    public void Cold_instance_reads_the_same_owner_records()
    {
        var owner = new FakeExternalIdempotencyStore();
        new StateServiceOfferRequestIndex(owner).Record("offer-2", "request-2", "jeeber-2");

        var cold = new StateServiceOfferRequestIndex(owner);

        cold.ResolveRequestId("offer-2").Should().Be("request-2");
        cold.ListOfferIdsForJeeber("jeeber-2").Should().Equal("offer-2");
    }

    [Fact]
    public void Owner_failure_is_not_hidden_by_a_local_fallback()
    {
        var index = new StateServiceOfferRequestIndex(new FaultingExternalIdempotencyStore());

        index.Invoking(value => value.Record("offer-3", "request-3", "jeeber-3"))
            .Should().Throw<InvalidOperationException>();
        index.Invoking(value => value.ResolveRequestId("offer-3"))
            .Should().Throw<InvalidOperationException>();
    }

    private sealed class FakeExternalIdempotencyStore : IExternalIdempotencyStore
    {
        private readonly ConcurrentDictionary<string, string> _values = new(StringComparer.Ordinal);

        public Task<IdempotencyOutcome> PutOrGetAsync(
            string key,
            int statusCode,
            string responseBodyJson,
            int ttlSeconds,
            CancellationToken ct)
        {
            var inserted = _values.TryAdd(key, responseBodyJson);
            return Task.FromResult(new IdempotencyOutcome
            {
                Inserted = inserted,
                StatusCode = statusCode,
                ResponseBodyJson = _values[key],
            });
        }

        public Task<IdempotencyOutcome?> GetAsync(string key, CancellationToken ct) =>
            Task.FromResult(_values.TryGetValue(key, out var body)
                ? new IdempotencyOutcome
                {
                    Inserted = false,
                    StatusCode = 200,
                    ResponseBodyJson = body,
                }
                : null);

        public Task<IReadOnlyList<IdempotencyOutcome>> FindByPrefixAsync(
            string prefix,
            CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<IdempotencyOutcome>>(_values
                .Where(pair => pair.Key.StartsWith(prefix, StringComparison.Ordinal))
                .Select(pair => new IdempotencyOutcome
                {
                    Inserted = false,
                    StatusCode = 200,
                    ResponseBodyJson = pair.Value,
                })
                .ToArray());
    }

    private sealed class FaultingExternalIdempotencyStore : IExternalIdempotencyStore
    {
        private static InvalidOperationException Error() => new("owner unavailable");

        public Task<IdempotencyOutcome> PutOrGetAsync(
            string key, int statusCode, string responseBodyJson, int ttlSeconds, CancellationToken ct) =>
            Task.FromException<IdempotencyOutcome>(Error());

        public Task<IdempotencyOutcome?> GetAsync(string key, CancellationToken ct) =>
            Task.FromException<IdempotencyOutcome?>(Error());

        public Task<IReadOnlyList<IdempotencyOutcome>> FindByPrefixAsync(
            string prefix, CancellationToken ct) =>
            Task.FromException<IReadOnlyList<IdempotencyOutcome>>(Error());
    }
}
