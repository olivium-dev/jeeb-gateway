using System.Collections.Concurrent;
using System.Net;
using JeebGateway.Services.Clients;
using JeebGateway.Tiers;

namespace JeebGateway.IntegrationTests;

/// <summary>
/// S06 shared test double: an in-process, presence-aware fake for
/// <see cref="IDeliveryServiceClient"/>. The gateway availability + location
/// controllers now write/read jeeber presence THROUGH delivery-service
/// (DELIVERY-SERVICE-RELOCATION-DESIGN.md §8). The real Go presence routes are
/// built in REPO 1 (delivery-service); for gateway integration tests we swap the
/// HTTP client for this fake so the toggle/GET/heartbeat response shapes resolve
/// without a live upstream.
///
/// Only the three S06 presence methods carry behaviour:
/// <list type="bullet">
///   <item><see cref="SetAvailabilityAsync"/> — upserts the row.</item>
///   <item><see cref="GetAvailabilityAsync"/> — returns the row, or null (never-online).</item>
///   <item><see cref="HeartbeatAsync"/> — bumps location, or throws a 404
///     <see cref="DeliveryAvailabilityException"/> when the jeeber never went online.</item>
/// </list>
/// Every other method throws <see cref="NotSupportedException"/> so an
/// accidental call is loud.
/// </summary>
internal sealed class FakeDeliveryPresenceClient : IDeliveryServiceClient
{
    private readonly ConcurrentDictionary<string, JeeberAvailabilityUpstream> _store = new();

    public Task<JeeberAvailabilityUpstream> SetAvailabilityAsync(JeeberAvailabilityUpstreamRequest body, string jeeberId, CancellationToken ct)
    {
        var row = new JeeberAvailabilityUpstream
        {
            JeeberId = jeeberId,
            Online = body.Online,
            VehicleType = body.VehicleType,
            Zone = body.Zone,
            Lat = body.Lat,
            Lng = body.Lng,
            LastSeenAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };
        _store[jeeberId] = row;
        return Task.FromResult(row);
    }

    public Task<JeeberAvailabilityUpstream?> GetAvailabilityAsync(string jeeberId, CancellationToken ct)
        => Task.FromResult(_store.TryGetValue(jeeberId, out var row) ? row : null);

    public Task<JeeberAvailabilityUpstream> HeartbeatAsync(string jeeberId, double lat, double lng, CancellationToken ct)
    {
        if (!_store.TryGetValue(jeeberId, out var existing))
        {
            // Mirrors the real upstream: a heartbeat for a jeeber who never went
            // online is a 404, which the controller maps to a non-500 outcome.
            throw new DeliveryAvailabilityException((int)HttpStatusCode.NotFound, "not_online");
        }

        var row = new JeeberAvailabilityUpstream
        {
            JeeberId = jeeberId,
            Online = existing.Online,
            VehicleType = existing.VehicleType,
            Zone = existing.Zone,
            Lat = lat,
            Lng = lng,
            LastSeenAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };
        _store[jeeberId] = row;
        return Task.FromResult(row);
    }

    public Task<int> CountActiveDeliveriesByJeeberAsync(string jeeberId, CancellationToken ct) => throw new NotSupportedException();
    public Task<IReadOnlyList<DeliveryTierDto>> ListTiersAsync(CancellationToken ct) => throw new NotSupportedException();
    public Task<ShipmentsListDto> ListShipmentsAsync(string? orderId, string? stage, int? limit, CancellationToken ct) => throw new NotSupportedException();
    public Task<DeliveryRequestUpstream> CreateRequestAsync(CreateDeliveryRequestUpstream body, CancellationToken ct) => throw new NotSupportedException();
    public Task<DeliveryRowUpstream> CreateDeliveryRowAsync(CreateDeliveryRowUpstream body, CancellationToken ct) => throw new NotSupportedException();
    public Task<DeliveryRequestUpstream> GetDeliveryAsync(string deliveryId, CancellationToken ct) => throw new NotSupportedException();
    public Task<DeliveryOtpVerifyResult> VerifyOtpAsync(string deliveryId, string otpCode, CancellationToken ct) => throw new NotSupportedException();
    public Task<DeliveryRequestUpstream> StatusTransitionAsync(string deliveryId, string status, CancellationToken ct) => throw new NotSupportedException();
    public Task<DeliveryTransitionUpstream> CanonicalTransitionAsync(string deliveryId, string to, string partySource, string actorId, string actorRole, CancellationToken ct) => throw new NotSupportedException();
    public Task<DeliveryReadUpstream?> GetCanonicalDeliveryAsync(string deliveryId, CancellationToken ct) => throw new NotSupportedException();
    public Task<DeliveryHandoverIssueResult> IssueHandoverOtpAsync(string deliveryId, string? codeHash, CancellationToken ct) => throw new NotSupportedException();
    public Task<DeliveryHandoverVerifyResult> VerifyHandoverOtpAsync(string deliveryId, bool success, string actorId, string actorRole, CancellationToken ct) => throw new NotSupportedException();
    public Task<DeliveryCancelResult> CancelDeliveryAsync(string deliveryId, DeliveryCancelUpstreamRequest body, CancellationToken ct) => throw new NotSupportedException();
    public Task<DeliveryMatchingRunResult> RunMatchingAsync(DeliveryMatchingRunRequest body, CancellationToken ct) => throw new NotSupportedException();
    public Task<JeeberFeedResult> GetJeeberFeedAsync(string jeeberId, int? limit, CancellationToken ct) => throw new NotSupportedException();
}
