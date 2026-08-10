using JeebGateway.Infrastructure;
using JeebGateway.Requests.OtpHandover;
using JeebGateway.Services.Clients;

namespace JeebGateway.Requests;

/// <summary>
/// Stateless compatibility adapter over delivery-service. Members for which
/// delivery-service has no owner route fail closed instead of retaining a local
/// request projection.
/// </summary>
public sealed class DeliveryOwnerRequestsStore : IRequestsStore
{
    private readonly IDeliveryServiceClient _owner;

    public DeliveryOwnerRequestsStore(IDeliveryServiceClient owner) => _owner = owner;

    public Task<int> CountActiveForJeeberAsync(string jeeberId, CancellationToken ct) =>
        _owner.CountActiveDeliveriesByJeeberAsync(jeeberId, ct);

    public async Task<DeliveryRequest> CreateAsync(CreateRequestInput input, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (input.PickupLocation is null || input.DropoffLocation is null
            || string.IsNullOrWhiteSpace(input.TierId))
        {
            throw new ArgumentException("Pickup, drop-off, and tier are required by delivery-service.", nameof(input));
        }

        var row = await _owner.CreateRequestAsync(new CreateDeliveryRequestUpstream
        {
            ClientId = input.ClientId,
            Description = input.Description,
            AudioUrl = input.AudioUrl,
            Photos = input.Photos,
            TierId = input.TierId,
            Pickup = new LatLngUpstream { Lat = input.PickupLocation.Lat, Lng = input.PickupLocation.Lng },
            Dropoff = new LatLngUpstream { Lat = input.DropoffLocation.Lat, Lng = input.DropoffLocation.Lng },
            PickupAddress = input.PickupAddress,
            DropoffAddress = input.DropoffAddress,
        }, ct);
        return Map(row);
    }

    public Task<DeliveryRequest> TryCreateWithLimitAsync(
        CreateRequestInput input, int limit, CancellationToken ct) => CreateAsync(input, ct);

    public async Task<DeliveryRequest?> GetAsync(string requestId, CancellationToken ct)
    {
        try
        {
            return Map(await _owner.GetDeliveryAsync(requestId, ct));
        }
        catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    public async Task<CancellationStoreResult?> TryCancelAsync(
        string requestId,
        IReadOnlySet<string> allowedFromStates,
        string targetStatus,
        string cancelledBy,
        string? reason,
        DateTimeOffset at,
        CancellationToken ct)
    {
        var current = await GetAsync(requestId, ct);
        if (current is null) return null;
        var result = await _owner.CancelDeliveryAsync(requestId, new DeliveryCancelUpstreamRequest
        {
            Role = cancelledBy,
            UserId = cancelledBy,
            Reason = reason,
        }, ct);
        return new CancellationStoreResult(
            CancellationStoreOutcome.Committed,
            Map(result.Request),
            current.Status);
    }

    public Task<int> CountActiveForClientAsync(string clientId, CancellationToken ct) =>
        Unsupported<int>("delivery-service active delivery count by client");
    public Task<bool> SetStatusAsync(string requestId, string status, CancellationToken ct) =>
        Unsupported<bool>("delivery-service compatibility status mutation");
    public Task<bool> SetJeeberIdAsync(string requestId, string jeeberId, CancellationToken ct) =>
        Unsupported<bool>("delivery-service compatibility assignee mutation");
    public Task<bool> TrySetAcceptedFeeAsync(string requestId, decimal fee, CancellationToken ct) =>
        Unsupported<bool>("delivery-service accepted-fee snapshot");
    public Task<IReadOnlyList<DeliveryRequest>> ListPendingCreatedAtOrBeforeAsync(DateTimeOffset cutoff, CancellationToken ct) =>
        Unsupported<IReadOnlyList<DeliveryRequest>>("delivery-service expiry candidate list");
    public Task<IReadOnlyList<DeliveryRequest>> ListAssignedSinceAsync(DateTimeOffset since, int limit, CancellationToken ct) =>
        Unsupported<IReadOnlyList<DeliveryRequest>>("delivery-service assigned delivery list");
    public Task<bool> TryExpireAsync(string requestId, DateTimeOffset at, CancellationToken ct) =>
        Unsupported<bool>("delivery-service request expiry mutation");
    public Task<int> AnonymizeForClientAsync(string userId, string anonymizedHash, CancellationToken ct) =>
        Unsupported<int>("delivery-service client anonymization");
    public Task<IReadOnlyList<DeliveryRequest>> ListScheduledDueAsync(DateTimeOffset cutoff, CancellationToken ct) =>
        Unsupported<IReadOnlyList<DeliveryRequest>>("delivery-service scheduled activation list");
    public Task<bool> TryActivateScheduledAsync(string requestId, DateTimeOffset at, CancellationToken ct) =>
        Unsupported<bool>("delivery-service scheduled activation mutation");
    public Task<IReadOnlyList<DeliveryRequest>> ListForClientAsync(string clientId, CancellationToken ct) =>
        Unsupported<IReadOnlyList<DeliveryRequest>>("delivery-service client-scoped delivery list");
    public Task<IReadOnlyList<DeliveryRequest>> ListForJeeberAsync(string jeeberId, CancellationToken ct) =>
        Unsupported<IReadOnlyList<DeliveryRequest>>("delivery-service jeeber-scoped delivery list");
    public Task<DeliveryRequest?> GetByConversationIdAsync(string conversationId, CancellationToken ct) =>
        Unsupported<DeliveryRequest?>("delivery-service lookup by conversation");
    public Task SetConversationIdAsync(string requestId, string conversationId, CancellationToken ct) =>
        Unsupported("delivery-service conversation projection");
    public Task<DeliveryRequest?> TryAcceptByJeeberAsync(string requestId, string jeeberId, int limit, DateTimeOffset at, CancellationToken ct) =>
        Unsupported<DeliveryRequest?>("offer-service accept orchestration");
    public Task<CancellationStoreResult?> TryDecideCancellationAsync(string requestId, bool approve, DateTimeOffset at, CancellationToken ct) =>
        Unsupported<CancellationStoreResult?>("delivery-service cancellation decision");
    public Task<(IReadOnlyList<DeliveryRequest> Items, int Total)> ListPendingCancellationsAsync(int page, int pageSize, CancellationToken ct) =>
        Unsupported<(IReadOnlyList<DeliveryRequest>, int)>("delivery-service pending cancellation list");
    public Task<IReadOnlyList<DeliveryRequest>> ListJeeberCancelledAsync(string jeeberId, CancellationToken ct) =>
        Unsupported<IReadOnlyList<DeliveryRequest>>("delivery-service jeeber cancellation history");
    public Task<OtpVerificationResult> TryVerifyOtpAsync(string requestId, string otpCode, int maxAttempts, DateTimeOffset at, CancellationToken ct) =>
        Unsupported<OtpVerificationResult>("delivery-service legacy OTP verification");
    public Task<DeliveryRequest?> MarkClientUnreachableAsync(string requestId, DateTimeOffset at, CancellationToken ct) =>
        Unsupported<DeliveryRequest?>("delivery-service unreachable marker");
    public Task<IReadOnlyList<DeliveryRequest>> ListUnreachableAtOrBeforeAsync(DateTimeOffset cutoff, CancellationToken ct) =>
        Unsupported<IReadOnlyList<DeliveryRequest>>("delivery-service unreachable candidate list");
    public Task<bool> TrySetEscalationIdAsync(string requestId, string escalationId, CancellationToken ct) =>
        Unsupported<bool>("delivery-service escalation projection");

    private static DeliveryRequest Map(DeliveryRequestUpstream row) => new()
    {
        Id = row.Id,
        ClientId = row.ClientId,
        Status = row.Status,
        Description = row.Description ?? string.Empty,
        AudioUrl = row.AudioUrl,
        Photos = row.Photos,
        TierId = row.TierId,
        PickupLocation = row.Pickup is null ? null : new GeoPoint { Lat = row.Pickup.Lat, Lng = row.Pickup.Lng },
        DropoffLocation = row.Dropoff is null ? null : new GeoPoint { Lat = row.Dropoff.Lat, Lng = row.Dropoff.Lng },
        PickupAddress = row.PickupAddress,
        DropoffAddress = row.DropoffAddress,
        JeeberId = row.JeeberId,
        AcceptedAt = row.AcceptedAt,
        GpsTrackingActive = row.GpsTrackingActive,
        OtpAttemptCount = row.OtpAttemptCount,
        OtpLockedAt = row.OtpLockedAt,
        OtpEscalationId = row.OtpEscalationId,
        RecipientPhone = row.RecipientPhone,
        ExpiredAt = row.ExpiresAt,
        CancelledBy = row.CancelledBy,
        CancellationReason = row.CancellationReason,
        CreatedAt = row.CreatedAt,
    };

    private static Task Unsupported(string capability) =>
        Task.FromException(new OwnerCapabilityUnavailableException(capability));

    private static Task<T> Unsupported<T>(string capability) =>
        Task.FromException<T>(new OwnerCapabilityUnavailableException(capability));
}
