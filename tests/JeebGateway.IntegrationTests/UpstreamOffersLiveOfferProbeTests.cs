using FluentAssertions;
using JeebGateway.Availability;
using JeebGateway.Services.Clients;
using Xunit;

namespace JeebGateway.IntegrationTests;

/// <summary>
/// Guards the live-wire half of the FR-6.6 nudge fix. The interface's DEFAULT
/// <see cref="IPendingOffersStore.HasLiveOfferForRequestAsync"/> routes through
/// <see cref="UpstreamPendingOffersStore.ListForRequestAsync"/>, which resolves identity from
/// <c>IHttpContextAccessor</c> and returns EMPTY with no HTTP context — so if the override
/// here were ever dropped, the nudge suppression would pass every in-memory test and be a
/// no-op in production (the sweeper is a background service). These tests fail in exactly
/// that case: they construct the store WITHOUT an accessor, as the sweeper effectively does.
/// </summary>
public class UpstreamOffersLiveOfferProbeTests
{
    private const string RequestId = "req-1";
    private const string OwnerId = "client-owner-1";

    [Theory]
    [InlineData("submitted")]
    [InlineData("pending")]
    [InlineData("edited")]
    [InlineData("accepted")]
    [InlineData("ACCEPTED")]
    public async Task Reports_Live_Offer_For_Every_Live_Upstream_Status(string wireStatus)
    {
        var client = new RecordingOfferServiceClient();
        client.RequestOffers[RequestId] = new List<OfferWire> { Wire("offer-1", wireStatus) };
        IPendingOffersStore store = new UpstreamPendingOffersStore(client);

        var hasLive = await store.HasLiveOfferForRequestAsync(RequestId, OwnerId, default);

        hasLive.Should().BeTrue();
        client.ActingUserIds.Should().ContainSingle().Which.Should().Be(
            OwnerId,
            "offer-service authorizes its request-scoped list on x-user-id == the request OWNER, "
            + "and a background sweeper has no HTTP context to resolve one from");
    }

    [Theory]
    [InlineData("withdrawn")]
    [InlineData("superseded")]
    [InlineData("rejected")]
    [InlineData("expired")]
    public async Task Reports_No_Live_Offer_For_Terminal_Only_Statuses(string wireStatus)
    {
        var client = new RecordingOfferServiceClient();
        client.RequestOffers[RequestId] = new List<OfferWire> { Wire("offer-1", wireStatus) };
        IPendingOffersStore store = new UpstreamPendingOffersStore(client);

        (await store.HasLiveOfferForRequestAsync(RequestId, OwnerId, default)).Should().BeFalse();
    }

    [Fact]
    public async Task Reports_No_Live_Offer_When_Request_Has_None()
    {
        IPendingOffersStore store = new UpstreamPendingOffersStore(new RecordingOfferServiceClient());

        (await store.HasLiveOfferForRequestAsync(RequestId, OwnerId, default)).Should().BeFalse();
    }

    [Fact]
    public async Task Degrades_To_No_Live_Offer_When_Upstream_Throws()
    {
        var client = new RecordingOfferServiceClient { Throw = true };
        IPendingOffersStore store = new UpstreamPendingOffersStore(client);

        (await store.HasLiveOfferForRequestAsync(RequestId, OwnerId, default)).Should().BeFalse(
            "a lookup blip must send a possibly-redundant nudge, never suppress a legitimate one");
    }

    [Fact]
    public async Task Skips_Upstream_Call_When_Owner_Is_Unknown()
    {
        var client = new RecordingOfferServiceClient();
        client.RequestOffers[RequestId] = new List<OfferWire> { Wire("offer-1", "submitted") };
        IPendingOffersStore store = new UpstreamPendingOffersStore(client);

        (await store.HasLiveOfferForRequestAsync(RequestId, null, default)).Should().BeFalse();
        client.ActingUserIds.Should().BeEmpty("the store must never guess an identity");
    }

    private static OfferWire Wire(string id, string status) => new()
    {
        Id = id,
        RequestId = RequestId,
        JeeberId = "jeeber-1",
        Status = status,
        FeeCents = 500,
        EtaMinutes = 20,
        CreatedAt = DateTimeOffset.UtcNow,
    };

    private sealed class RecordingOfferServiceClient : IOfferServiceClient
    {
        public Dictionary<string, List<OfferWire>> RequestOffers { get; } = new(StringComparer.Ordinal);
        public List<string> ActingUserIds { get; } = new();
        public bool Throw { get; init; }

        public Task<IReadOnlyList<OfferWire>> ListForRequestAsync(
            string actingUserId, string requestId, CancellationToken ct)
        {
            ActingUserIds.Add(actingUserId);
            if (Throw) throw new HttpRequestException("offer-service unavailable (test double)");
            return Task.FromResult<IReadOnlyList<OfferWire>>(
                RequestOffers.TryGetValue(requestId, out var offers)
                    ? offers
                    : Array.Empty<OfferWire>());
        }

        public Task<RequestMirrorResult> MirrorRequestAsync(
            string actingUserId, string requestId, string clientId, CancellationToken ct)
            => throw new NotSupportedException("not exercised by these tests");

        public Task<OfferWire> SubmitAsync(
            string actingUserId, string requestId, long feeCents, int etaMinutes, string? note,
            CancellationToken ct)
            => throw new NotSupportedException("not exercised by these tests");

        public Task<OfferWithdrawResult> WithdrawAsync(
            string actingUserId, string requestId, string offerId, CancellationToken ct)
            => throw new NotSupportedException("not exercised by these tests");

        public Task<OfferAcceptWire> AcceptAsync(
            string actingUserId, string requestId, string offerId, string idempotencyKey,
            CancellationToken ct)
            => throw new NotSupportedException("not exercised by these tests");

        public Task<OfferAcceptResult> AcceptWithStatusAsync(
            string actingUserId, string requestId, string offerId, string idempotencyKey,
            CancellationToken ct)
            => throw new NotSupportedException("not exercised by these tests");

        public Task<OfferMutationResult> EditAsync(
            string actingUserId, string requestId, string offerId, long? feeCents,
            int? etaMinutes, string? note, int? maxEdits, CancellationToken ct)
            => throw new NotSupportedException("not exercised by these tests");

        public Task<OfferMutationResult> RejectAsync(
            string actingUserId, string offerId, CancellationToken ct)
            => throw new NotSupportedException("not exercised by these tests");
    }
}
