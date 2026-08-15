using JeebGateway.Infrastructure;
using JeebGateway.Requests.OtpHandover;

namespace JeebGateway.Requests;

/// W5-04 stateless IRequestsStore over delivery-service's request-owner surface (HTTP only, G-21);
/// selected by FeatureFlags:RequestsOwnerListMode=upstream-authority. Only TryVerifyOtpAsync fails closed.
public sealed class UpstreamRequestsStore : IRequestsStore
{
    // ActiveStates under the owner's status-count fold (canonical for mapped
    // legacy actives, raw pass-through for the rest).
    private static readonly IReadOnlySet<string> ClientActiveOwnerTokens =
        new HashSet<string>(StringComparer.Ordinal)
        {
            RequestStatus.Scheduled, RequestStatus.Pending, RequestStatus.Matched,
            RequestStatus.CancellationRequested,
            "Ordered", "Picked", "InTransit", "AtDoor",
        };

    // JeeberActiveStates under the same fold.
    private static readonly IReadOnlySet<string> JeeberActiveOwnerTokens =
        new HashSet<string>(StringComparer.Ordinal)
        {
            RequestStatus.CancellationRequested,
            "Ordered", "Picked", "InTransit", "AtDoor",
        };

    private const string ClientRole = "client";
    private const string ProviderRole = "provider";

    // The owner clamps every list at 500 rows; ask for the full window.
    private const int OwnerListLimit = 500;

    private readonly IRequestsOwnerClient _owner;
    private readonly TimeProvider _clock;
    private readonly ILogger<UpstreamRequestsStore> _logger;

    public UpstreamRequestsStore(
        IRequestsOwnerClient owner,
        TimeProvider clock,
        ILogger<UpstreamRequestsStore> logger)
    {
        _owner = owner;
        _clock = clock;
        _logger = logger;
    }

    // ---- create ------------------------------------------------------------

    public async Task<DeliveryRequest> CreateAsync(CreateRequestInput input, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(input);
        var id = string.IsNullOrWhiteSpace(input.Id) ? Guid.NewGuid().ToString() : input.Id!;
        var createdAt = _clock.GetUtcNow();

        await _owner.UpsertAsync(new UpsertRequestUpstream
        {
            RequestId = id,
            ClientId = input.ClientId,
            Status = input.ScheduledAt is null ? RequestStatus.Pending : RequestStatus.Scheduled,
            Description = input.Description,
            Transcription = input.Transcription,
            TranscriptionConfidence = input.TranscriptionConfidence,
            AudioUrl = input.AudioUrl,
            Photos = input.Photos,
            TierId = input.TierId,
            PickupAddress = input.PickupAddress,
            DropoffAddress = input.DropoffAddress,
            PickupLat = input.PickupLocation?.Lat,
            PickupLng = input.PickupLocation?.Lng,
            DropoffLat = input.DropoffLocation?.Lat,
            DropoffLng = input.DropoffLocation?.Lng,
            RecipientPhone = input.RecipientPhone,
            ScheduledAt = input.ScheduledAt?.UtcDateTime.ToString("O"),
            CreatedAt = createdAt.UtcDateTime.ToString("O"),
        }, ct);

        // DO-NOTHING upsert + read-back: fresh create and idempotent voice
        // replay both return the stored row.
        var row = await _owner.GetByIdAsync(id, ct)
            ?? throw new InvalidOperationException(
                $"delivery-service acknowledged request '{id}' but does not serve it back.");
        return Map(row);
    }

    public Task<DeliveryRequest> TryCreateWithLimitAsync(
        CreateRequestInput input, int limit, CancellationToken ct)
        // BR-9 is enforced upstream; its 409 surfaces as
        // TooManyActiveRequestsException from the owner client.
        => CreateAsync(input, ct);

    // ---- reads -------------------------------------------------------------

    public async Task<DeliveryRequest?> GetAsync(string requestId, CancellationToken ct)
        => MapOrNull(await _owner.GetByIdAsync(requestId, ct));

    public async Task<IReadOnlyList<DeliveryRequest>> ListForClientAsync(
        string clientId, CancellationToken ct)
        => MapAll(await _owner.ListRecordsAsync(ClientRole, clientId, oldestFirst: true, OwnerListLimit, ct));

    public async Task<IReadOnlyList<DeliveryRequest>> ListForJeeberAsync(
        string jeeberId, CancellationToken ct)
        => MapAll(await _owner.ListRecordsAsync(ProviderRole, jeeberId, oldestFirst: false, OwnerListLimit, ct));

    public async Task<DeliveryRequest?> GetByConversationIdAsync(
        string conversationId, CancellationToken ct)
        => string.IsNullOrWhiteSpace(conversationId)
            ? null
            : MapOrNull(await _owner.ByConversationAsync(conversationId, ct));

    public async Task<int> CountActiveForClientAsync(string clientId, CancellationToken ct)
        => CountTokens(await _owner.StatusCountsAsync(ClientRole, clientId, ct), ClientActiveOwnerTokens);

    public async Task<int> CountActiveForJeeberAsync(string jeeberId, CancellationToken ct)
        => CountTokens(await _owner.StatusCountsAsync(ProviderRole, jeeberId, ct), JeeberActiveOwnerTokens);

    public async Task<IReadOnlyList<DeliveryRequest>> ListPendingCreatedAtOrBeforeAsync(
        DateTimeOffset cutoff, CancellationToken ct)
        => MapAll(await _owner.DueAsync("expiry", cutoff, OwnerListLimit, ct));

    public async Task<IReadOnlyList<DeliveryRequest>> ListScheduledDueAsync(
        DateTimeOffset cutoff, CancellationToken ct)
        => MapAll(await _owner.DueAsync("activation", cutoff, OwnerListLimit, ct));

    public async Task<IReadOnlyList<DeliveryRequest>> ListAssignedSinceAsync(
        DateTimeOffset since, int limit, CancellationToken ct)
        => limit <= 0
            ? Array.Empty<DeliveryRequest>()
            : MapAll(await _owner.ListAssignedSinceAsync(since, limit, ct));

    public async Task<RequestsAdminSnapshot> GetAdminDashboardSnapshotAsync(
        int recentLimit, CancellationToken ct)
    {
        var summary = await _owner.GetSummaryAsync(Math.Max(recentLimit, 0), ct);
        return new RequestsAdminSnapshot
        {
            CountsByStatus = new Dictionary<string, int>(summary.Counts, StringComparer.Ordinal),
            Recent = summary.Recent.Select(r => new RequestsAdminRecentRow
            {
                Id = r.RequestId,
                Status = r.Status,
                Title = r.Title ?? r.Description ?? string.Empty,
                ClientId = r.ClientId,
                JeeberId = NullIfEmpty(r.ProviderId),
                UpdatedAt = r.UpdatedAt ?? r.CreatedAt,
            }).ToList(),
        };
    }

    // ---- lifecycle writes --------------------------------------------------

    public Task<bool> SetStatusAsync(string requestId, string status, CancellationToken ct)
        => _owner.SetStatusAsync(requestId, status, ct);

    public Task<bool> SetJeeberIdAsync(string requestId, string jeeberId, CancellationToken ct)
        // Blank never clears an assignee — same no-op-false as the local store.
        => string.IsNullOrWhiteSpace(jeeberId)
            ? Task.FromResult(false)
            : _owner.SetAssigneeAsync(requestId, jeeberId, ct);

    public Task<bool> TrySetAcceptedFeeAsync(string requestId, decimal fee, CancellationToken ct)
        => fee <= 0m ? Task.FromResult(false) : _owner.SetAcceptedFeeAsync(requestId, fee, ct);

    public Task<bool> TryExpireAsync(string requestId, DateTimeOffset at, CancellationToken ct)
        => _owner.ExpireAsync(requestId, ct);

    public Task<bool> TryActivateScheduledAsync(string requestId, DateTimeOffset at, CancellationToken ct)
        => _owner.ActivateAsync(requestId, ct);

    public async Task<int> AnonymizeForClientAsync(
        string userId, string anonymizedHash, CancellationToken ct)
        => (int)Math.Min(await _owner.AnonymizeAsync(userId, anonymizedHash, ct), int.MaxValue);

    public async Task SetConversationIdAsync(
        string requestId, string conversationId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(requestId) || string.IsNullOrWhiteSpace(conversationId))
        {
            return;
        }
        try
        {
            await _owner.StampConversationAsync(requestId, conversationId, ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Best-effort by contract: the accept saga has already committed.
            _logger.LogWarning(ex,
                "requests-upstream: conversation stamp failed for {RequestId}.", requestId);
        }
    }

    public async Task<DeliveryRequest?> TryAcceptByJeeberAsync(
        string requestId, string jeeberId, int limit, DateTimeOffset at, CancellationToken ct)
    {
        var result = await _owner.AcceptAsync(requestId, jeeberId, ct);
        return result.Outcome switch
        {
            AcceptRequestOutcome.NotFound => null,
            AcceptRequestOutcome.Accepted or AcceptRequestOutcome.AlreadyMine =>
                MapOrNull(await _owner.GetByIdAsync(requestId, ct)),
            _ => throw new RequestNotAcceptableException(result.CurrentStatus ?? "unknown"),
        };
    }

    // ---- cancellation ------------------------------------------------------

    public async Task<CancellationStoreResult?> TryCancelAsync(
        string requestId,
        IReadOnlySet<string> allowedFromStates,
        string targetStatus,
        string cancelledBy,
        string? reason,
        DateTimeOffset at,
        CancellationToken ct)
        => MapCancel(await _owner.CancelGuardedAsync(
            requestId, allowedFromStates.ToArray(), targetStatus, cancelledBy, reason, ct));

    public async Task<CancellationStoreResult?> TryDecideCancellationAsync(
        string requestId, bool approve, DateTimeOffset at, CancellationToken ct)
        // The reject fallback mirrors the local store: resume inside the active
        // fulfilment lane when no previous status was recorded.
        => MapCancel(await _owner.DecideCancellationAsync(
            requestId, approve, RequestStatus.PickedUp, ct));

    public async Task<(IReadOnlyList<DeliveryRequest> Items, int Total)> ListPendingCancellationsAsync(
        int page, int pageSize, CancellationToken ct)
    {
        var (items, total) = await _owner.ListPendingCancellationsAsync(page, pageSize, ct);
        return (MapAll(items), total);
    }

    public async Task<IReadOnlyList<DeliveryRequest>> ListJeeberCancelledAsync(
        string jeeberId, CancellationToken ct)
        => MapAll(await _owner.ListCancelledByAssigneeAsync(jeeberId, "jeeber", ct));

    // ---- handover ----------------------------------------------------------

    public Task<OtpVerificationResult> TryVerifyOtpAsync(
        string requestId, string otpCode, int maxAttempts, DateTimeOffset at, CancellationToken ct)
        // handover_otp owns the code upstream; the canonical at-door verify is
        // the UseUpstream:Delivery surface.
        => Task.FromException<OtpVerificationResult>(new OwnerCapabilityUnavailableException(
            "request-record handover-OTP verification (served by the delivery-service handover surface)"));

    public async Task<DeliveryRequest?> MarkClientUnreachableAsync(
        string requestId, DateTimeOffset at, CancellationToken ct)
        => MapOrNull(await _owner.MarkUnreachableAsync(requestId, ct));

    public async Task<IReadOnlyList<DeliveryRequest>> ListUnreachableAtOrBeforeAsync(
        DateTimeOffset cutoff, CancellationToken ct)
        => MapAll(await _owner.ListUnreachableAsync(cutoff, OwnerListLimit, ct));

    public Task<bool> TrySetEscalationIdAsync(
        string requestId, string escalationId, CancellationToken ct)
        => _owner.SetEscalationRefAsync(requestId, escalationId, ct);

    // ---- mapping -----------------------------------------------------------

    /// Owner record → gateway model; public static for HTTP-free unit tests.
    /// DeliveryOtp/attempt fields stay default: the owner never stores the code.
    public static DeliveryRequest Map(RequestOwnerRow row) => new()
    {
        Id = row.RequestId,
        ClientId = row.ClientId,
        Status = row.Status,
        Description = row.Description ?? string.Empty,
        Transcription = row.Transcription,
        TranscriptionConfidence = row.TranscriptionConfidence,
        AudioUrl = row.AudioUrl,
        Photos = row.Photos ?? Array.Empty<string>(),
        TierId = row.TierId,
        PickupLocation = Point(row.PickupLat, row.PickupLng),
        DropoffLocation = Point(row.DropoffLat, row.DropoffLng),
        PickupAddress = row.PickupAddress,
        DropoffAddress = row.DropoffAddress,
        RecipientPhone = row.RecipientPhone,
        CreatedAt = row.CreatedAt,
        ScheduledAt = row.ScheduledAt,
        ActivatedAt = row.ActivatedAt,
        ExpiredAt = row.ExpiredAt,
        JeeberId = NullIfEmpty(row.ProviderId),
        AcceptedAt = row.AcceptedAt,
        AcceptedFee = row.AcceptedFee,
        ConversationId = NullIfEmpty(row.ConversationId),
        ClientUnreachableAt = row.UnreachableAt,
        OtpEscalationId = row.EscalationRef,
        GpsTrackingActive = row.GpsTrackingActive,
        CancelledBy = row.CancelledBy,
        CancellationReason = row.CancellationReason,
        CancellationRequestedAt = row.CancellationRequestedAt,
        CancellationApprovedAt = row.CancellationApprovedAt,
        CancellationRejectedAt = row.CancellationRejectedAt,
        CancellationPreviousStatus = row.CancellationPreviousStatus,
    };

    private static DeliveryRequest? MapOrNull(RequestOwnerRow? row)
        => row is null ? null : Map(row);

    private static IReadOnlyList<DeliveryRequest> MapAll(IReadOnlyList<RequestOwnerRow> rows)
        => rows.Select(Map).ToArray();

    private static CancellationStoreResult? MapCancel(OwnerGuardedCancelResult? result)
        => result is null
            ? null
            : new CancellationStoreResult(
                result.Committed
                    ? CancellationStoreOutcome.Committed
                    : CancellationStoreOutcome.NotCancellable,
                Map(result.Record),
                result.PreviousStatus);

    private static GeoPoint? Point(double? lat, double? lng)
        => lat is null || lng is null ? null : new GeoPoint { Lat = lat.Value, Lng = lng.Value };

    private static string? NullIfEmpty(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value;

    private static int CountTokens(IReadOnlyDictionary<string, int> counts, IReadOnlySet<string> tokens)
    {
        var total = 0;
        foreach (var (token, count) in counts)
        {
            if (tokens.Contains(token))
            {
                total += count;
            }
        }
        return total;
    }
}
