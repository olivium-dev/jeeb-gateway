using System.Collections.Concurrent;
using JeebGateway.Requests.OtpHandover;

namespace JeebGateway.Requests;

/// <summary>
/// MVP-grade in-memory delivery request store. Backs the BR-9 concurrency
/// check until the downstream delivery-service (Postgres-backed) is wired
/// in via an NSwag-generated client.
///
/// All public methods are safe under concurrent access. The count + insert
/// pair in <see cref="TryCreateAsync"/> is performed under a write lock so
/// the BR-9 check is atomic — without it, two simultaneous creates at the
/// boundary could both observe 2 active and both proceed, letting the
/// client end up with 4 active requests.
/// </summary>
public class InMemoryRequestsStore : IRequestsStore
{
    private readonly ConcurrentDictionary<string, DeliveryRequest> _requests = new();
    private readonly object _writeLock = new();
    private readonly TimeProvider _clock;

    // Resolved from DI so test fakes (FakeClock in RequestExpirySweeperTests)
    // drive CreatedAt the same way they drive the sweeper. Without this the
    // store would stamp wall-clock UtcNow while the sweeper computes its
    // cutoff against the fake clock, and no candidate would ever match.
    public InMemoryRequestsStore(TimeProvider clock)
    {
        _clock = clock;
    }

    public Task<int> CountActiveForClientAsync(string clientId, CancellationToken ct)
    {
        return Task.FromResult(CountActiveUnlocked(clientId));
    }

    public Task<int> CountActiveForJeeberAsync(string jeeberId, CancellationToken ct)
    {
        // No lock required — ConcurrentDictionary enumeration is safe and a
        // snapshot may be stale but never torn. The atomic-cap path
        // (TryAcceptByJeeberAsync) re-counts under the write lock so this
        // is read-only.
        return Task.FromResult(CountJeeberActiveUnlocked(jeeberId));
    }

    public Task<DeliveryRequest> CreateAsync(CreateRequestInput input, CancellationToken ct)
    {
        // Unconditional create — callers that need the BR-9 atomic check
        // should use TryCreateWithLimitAsync instead.
        var req = BuildRequest(input);
        _requests[req.Id] = req;
        return Task.FromResult(req);
    }

    public Task<DeliveryRequest?> GetAsync(string requestId, CancellationToken ct)
    {
        _requests.TryGetValue(requestId, out var existing);
        return Task.FromResult<DeliveryRequest?>(existing);
    }

    public Task<IReadOnlyList<DeliveryRequest>> ListScheduledDueAsync(
        DateTimeOffset cutoff,
        CancellationToken ct)
    {
        lock (_writeLock)
        {
            IReadOnlyList<DeliveryRequest> snapshot = _requests.Values
                .Where(r => r.Status == RequestStatus.Scheduled
                            && r.ScheduledAt is not null
                            && r.ScheduledAt.Value <= cutoff)
                .ToArray();
            return Task.FromResult(snapshot);
        }
    }

    public Task<bool> TryActivateScheduledAsync(string requestId, DateTimeOffset at, CancellationToken ct)
    {
        lock (_writeLock)
        {
            if (!_requests.TryGetValue(requestId, out var existing))
            {
                return Task.FromResult(false);
            }
            // Only an unactivated scheduled row may transition. Anything else
            // (cancelled-before-window, already-activated, or impossible flows
            // like a manual flip to matched/accepted) is a no-op so the
            // activator never double-fires the reminder.
            if (existing.Status != RequestStatus.Scheduled || existing.ActivatedAt is not null)
            {
                return Task.FromResult(false);
            }
            existing.Status = RequestStatus.Pending;
            existing.ActivatedAt = at;
            return Task.FromResult(true);
        }
    }

    public Task<bool> SetStatusAsync(string requestId, string status, CancellationToken ct)
    {
        // Terminal-state guard: once a request lands in expired/cancelled/
        // delivered/rated/disputed it must not flip back. The expiry
        // sweeper depends on this — without it, a downstream offer-accept
        // racing the sweeper could undo the expiry and silently re-open
        // the request to new offers.
        lock (_writeLock)
        {
            if (!_requests.TryGetValue(requestId, out var existing))
            {
                return Task.FromResult(false);
            }

            if (RequestStatus.IsTerminal(existing.Status))
            {
                return Task.FromResult(false);
            }

            existing.Status = status;
            return Task.FromResult(true);
        }
    }

    public Task<IReadOnlyList<DeliveryRequest>> ListPendingCreatedAtOrBeforeAsync(
        DateTimeOffset cutoff,
        CancellationToken ct)
    {
        // Snapshot under the write lock so the sweeper sees a consistent
        // view; the dictionary is concurrent but a read can otherwise
        // race a status flip mid-iteration.
        lock (_writeLock)
        {
            IReadOnlyList<DeliveryRequest> snapshot = _requests.Values
                .Where(r => r.CreatedAt <= cutoff && RequestStatus.IsPreAcceptance(r.Status))
                .ToArray();
            return Task.FromResult(snapshot);
        }
    }

    public Task<bool> MarkNudgedAsync(string requestId, DateTimeOffset at, CancellationToken ct)
    {
        lock (_writeLock)
        {
            if (!_requests.TryGetValue(requestId, out var existing))
            {
                return Task.FromResult(false);
            }
            if (existing.NudgedAt is not null)
            {
                return Task.FromResult(false);
            }
            existing.NudgedAt = at;
            return Task.FromResult(true);
        }
    }

    public Task<bool> TryExpireAsync(string requestId, DateTimeOffset at, CancellationToken ct)
    {
        lock (_writeLock)
        {
            if (!_requests.TryGetValue(requestId, out var existing))
            {
                return Task.FromResult(false);
            }
            // Only pre-acceptance requests are expirable. Once an offer
            // has been accepted (or the request is already terminal),
            // expiry is a no-op.
            if (!RequestStatus.IsPreAcceptance(existing.Status))
            {
                return Task.FromResult(false);
            }
            existing.Status = RequestStatus.Expired;
            existing.ExpiredAt = at;
            return Task.FromResult(true);
        }
    }

    /// <summary>
    /// Atomic count + insert under the store's write lock. Returns the
    /// created request when the active count for <paramref name="input"/>'s
    /// client is below <paramref name="limit"/>; throws
    /// <see cref="TooManyActiveRequestsException"/> otherwise.
    /// </summary>
    public Task<DeliveryRequest> TryCreateWithLimitAsync(
        CreateRequestInput input,
        int limit,
        CancellationToken ct)
    {
        lock (_writeLock)
        {
            var active = CountActiveUnlocked(input.ClientId);
            if (active >= limit)
            {
                throw new TooManyActiveRequestsException(active, limit);
            }

            var req = BuildRequest(input);
            _requests[req.Id] = req;
            return Task.FromResult(req);
        }
    }

    /// <summary>
    /// BR-10 atomic accept. The count-then-set-status sequence is held
    /// under the same write lock so two simultaneous accepts at the
    /// 1→2 boundary cannot both observe 1 active and both succeed,
    /// leaving the Jeeber on 3.
    /// </summary>
    public Task<DeliveryRequest?> TryAcceptByJeeberAsync(
        string requestId,
        string jeeberId,
        int limit,
        DateTimeOffset at,
        CancellationToken ct)
    {
        lock (_writeLock)
        {
            if (!_requests.TryGetValue(requestId, out var existing))
            {
                return Task.FromResult<DeliveryRequest?>(null);
            }

            // The request must still be in the pre-acceptance set — anything
            // else (already accepted by another Jeeber, expired by the
            // sweeper, cancelled by the Client) loses the race. Surface it
            // as a distinct exception so the controller maps to a different
            // ProblemDetails type than the BR-10 cap conflict.
            if (!RequestStatus.IsPreAcceptance(existing.Status))
            {
                throw new RequestNotAcceptableException(existing.Status);
            }

            var active = CountJeeberActiveUnlocked(jeeberId);
            if (active >= limit)
            {
                throw new TooManyActiveDeliveriesException(active, limit);
            }

            existing.Status = RequestStatus.Accepted;
            existing.JeeberId = jeeberId;
            existing.AcceptedAt = at;
            // T-backend-013: issue the hand-off OTP at accept time so the
            // heading_off → delivered transition can verify it without an
            // extra round-trip. Production swap binds to a hashed column.
            if (string.IsNullOrEmpty(existing.DeliveryOtp))
            {
                existing.DeliveryOtp = GenerateOtp();
            }
            return Task.FromResult<DeliveryRequest?>(existing);
        }
    }

    /// <summary>
    /// T-backend-013 state-machine transition. The whole guard — status
    /// read, machine validation, OTP check, GPS flip — runs under the
    /// write lock so two PATCH calls racing at the same step cannot both
    /// commit.
    /// </summary>
    public Task<DeliveryTransitionResult> TryTransitionAsync(
        string requestId,
        string toStatus,
        string? otp,
        CancellationToken ct)
    {
        lock (_writeLock)
        {
            if (!_requests.TryGetValue(requestId, out var existing))
            {
                return Task.FromResult(new DeliveryTransitionResult(
                    DeliveryTransitionOutcome.NotFound, null, null, null));
            }

            var from = existing.Status;
            var validation = DeliveryStateMachine.ValidateTransition(from, toStatus);
            if (!validation.IsValid)
            {
                return Task.FromResult(new DeliveryTransitionResult(
                    DeliveryTransitionOutcome.InvalidTransition,
                    existing,
                    from,
                    validation.Reason));
            }

            if (DeliveryStateMachine.RequiresOtp(from, toStatus))
            {
                if (string.IsNullOrWhiteSpace(otp))
                {
                    return Task.FromResult(new DeliveryTransitionResult(
                        DeliveryTransitionOutcome.OtpRequired,
                        existing,
                        from,
                        "OTP is required to mark the delivery as delivered."));
                }
                if (!string.Equals(otp, existing.DeliveryOtp, StringComparison.Ordinal))
                {
                    return Task.FromResult(new DeliveryTransitionResult(
                        DeliveryTransitionOutcome.OtpMismatch,
                        existing,
                        from,
                        "Supplied OTP does not match."));
                }
            }

            existing.Status = toStatus;

            if (DeliveryStateMachine.ActivatesGpsTracking(from, toStatus))
            {
                existing.GpsTrackingActive = true;
            }

            return Task.FromResult(new DeliveryTransitionResult(
                DeliveryTransitionOutcome.Committed,
                existing,
                from,
                null));
        }
    }

    /// <summary>
    /// T-backend-015 dedicated OTP verification. Distinct from
    /// <see cref="TryTransitionAsync"/> because it owns the
    /// attempt-counter, lockout, and (via the caller) escalation
    /// semantics that the plain state-machine PATCH does not carry.
    /// </summary>
    public Task<OtpVerificationResult> TryVerifyOtpAsync(
        string requestId,
        string otpCode,
        int maxAttempts,
        DateTimeOffset at,
        CancellationToken ct)
    {
        lock (_writeLock)
        {
            if (!_requests.TryGetValue(requestId, out var existing))
            {
                return Task.FromResult(new OtpVerificationResult(
                    OtpVerificationOutcome.NotFound, null, 0, false));
            }

            // Already locked from a previous attempt — return locked
            // without incrementing the counter or flipping any state.
            // JustLockedOut stays false so the controller does NOT
            // re-create the escalation row.
            if (existing.OtpLockedAt is not null)
            {
                return Task.FromResult(new OtpVerificationResult(
                    OtpVerificationOutcome.Locked, existing, 0, JustLockedOut: false));
            }

            // OTP verification is only valid at the documented handover
            // step. A caller racing the state machine (or hitting the
            // endpoint at the wrong moment) gets a deterministic 400.
            if (!string.Equals(existing.Status, RequestStatus.HeadingOff, StringComparison.Ordinal))
            {
                return Task.FromResult(new OtpVerificationResult(
                    OtpVerificationOutcome.NotInHandoverState,
                    existing,
                    Math.Max(0, maxAttempts - existing.OtpAttemptCount),
                    false));
            }

            // Success path — flip to delivered and zero out the counter
            // (defensive, in case the row had legitimate attempts
            // before the correct code landed).
            if (string.Equals(otpCode, existing.DeliveryOtp, StringComparison.Ordinal))
            {
                existing.Status = RequestStatus.Delivered;
                return Task.FromResult(new OtpVerificationResult(
                    OtpVerificationOutcome.Verified,
                    existing,
                    Math.Max(0, maxAttempts - existing.OtpAttemptCount),
                    false));
            }

            // Wrong OTP — increment the counter. If this attempt is the
            // one that hits the ceiling, stamp the lockout timestamp
            // and return Locked with JustLockedOut=true so the
            // controller fires exactly one escalation.
            existing.OtpAttemptCount++;
            if (existing.OtpAttemptCount >= maxAttempts)
            {
                existing.OtpLockedAt = at;
                return Task.FromResult(new OtpVerificationResult(
                    OtpVerificationOutcome.Locked,
                    existing,
                    0,
                    JustLockedOut: true));
            }

            return Task.FromResult(new OtpVerificationResult(
                OtpVerificationOutcome.Mismatch,
                existing,
                maxAttempts - existing.OtpAttemptCount,
                false));
        }
    }

    public Task<DeliveryRequest?> MarkClientUnreachableAsync(
        string requestId,
        DateTimeOffset at,
        CancellationToken ct)
    {
        lock (_writeLock)
        {
            if (!_requests.TryGetValue(requestId, out var existing))
            {
                return Task.FromResult<DeliveryRequest?>(null);
            }
            // Terminal rows cannot be flagged — once delivered/rated/
            // cancelled/expired/disputed, the handover window is over.
            if (RequestStatus.IsTerminal(existing.Status))
            {
                return Task.FromResult<DeliveryRequest?>(null);
            }
            // First-flag-wins: idempotent so repeated clicks don't
            // reset the 15-min sweeper timer.
            if (existing.ClientUnreachableAt is null)
            {
                existing.ClientUnreachableAt = at;
            }
            return Task.FromResult<DeliveryRequest?>(existing);
        }
    }

    public Task<IReadOnlyList<DeliveryRequest>> ListUnreachableAtOrBeforeAsync(
        DateTimeOffset cutoff,
        CancellationToken ct)
    {
        lock (_writeLock)
        {
            IReadOnlyList<DeliveryRequest> snapshot = _requests.Values
                .Where(r => r.ClientUnreachableAt is { } u
                    && u <= cutoff
                    && r.OtpEscalationId is null
                    && !RequestStatus.IsTerminal(r.Status))
                .ToArray();
            return Task.FromResult(snapshot);
        }
    }

    public Task<bool> TrySetEscalationIdAsync(
        string requestId,
        string escalationId,
        CancellationToken ct)
    {
        lock (_writeLock)
        {
            if (!_requests.TryGetValue(requestId, out var existing))
            {
                return Task.FromResult(false);
            }
            if (existing.OtpEscalationId is not null)
            {
                return Task.FromResult(false);
            }
            existing.OtpEscalationId = escalationId;
            return Task.FromResult(true);
        }
    }

    /// <summary>
    /// Six-digit numeric hand-off OTP (T-backend-013). MVP uses
    /// <see cref="Random.Shared"/>; production swaps to a cryptographically
    /// random generator hashed at the column.
    /// </summary>
    private static string GenerateOtp() => Random.Shared.Next(0, 1_000_000).ToString("D6");

    /// <summary>
    /// T-backend-024 (JEEB-42): atomic cancellation. The guard read + status
    /// flip + audit-field stamping all run under the write lock so a racing
    /// status PATCH or accept cannot land between them.
    /// </summary>
    public Task<CancellationStoreResult?> TryCancelAsync(
        string requestId,
        IReadOnlySet<string> allowedFromStates,
        string targetStatus,
        string cancelledBy,
        string? reason,
        DateTimeOffset at,
        CancellationToken ct)
    {
        lock (_writeLock)
        {
            if (!_requests.TryGetValue(requestId, out var existing))
            {
                return Task.FromResult<CancellationStoreResult?>(null);
            }

            var from = existing.Status;
            if (!allowedFromStates.Contains(from))
            {
                return Task.FromResult<CancellationStoreResult?>(
                    new CancellationStoreResult(CancellationStoreOutcome.NotCancellable, existing, from));
            }

            existing.CancellationPreviousStatus = from;
            existing.CancelledBy = cancelledBy;
            existing.CancellationReason = reason;
            existing.CancellationRequestedAt = at;
            existing.Status = targetStatus;
            // GPS tracking must stop the moment a delivery is cancelled —
            // continuing to ingest the Jeeber's location after the row
            // closes would leak telemetry the user no longer consents to.
            if (RequestStatus.IsTerminal(targetStatus))
            {
                existing.GpsTrackingActive = false;
            }

            return Task.FromResult<CancellationStoreResult?>(
                new CancellationStoreResult(CancellationStoreOutcome.Committed, existing, from));
        }
    }

    public Task<CancellationStoreResult?> TryDecideCancellationAsync(
        string requestId,
        bool approve,
        DateTimeOffset at,
        CancellationToken ct)
    {
        lock (_writeLock)
        {
            if (!_requests.TryGetValue(requestId, out var existing))
            {
                return Task.FromResult<CancellationStoreResult?>(null);
            }
            if (existing.Status != RequestStatus.CancellationRequested)
            {
                return Task.FromResult<CancellationStoreResult?>(null);
            }

            var from = existing.Status;
            if (approve)
            {
                existing.Status = RequestStatus.Cancelled;
                existing.CancellationApprovedAt = at;
                existing.GpsTrackingActive = false;
            }
            else
            {
                // Reject — revert to whatever lane the row was on before
                // the Client requested the cancel. Without a recorded
                // previous status (defensive), fall back to picked_up so
                // the delivery resumes inside the active fulfilment lane
                // rather than bouncing all the way back to pending.
                existing.Status = existing.CancellationPreviousStatus ?? RequestStatus.PickedUp;
                existing.CancellationRejectedAt = at;
                existing.CancelledBy = null;
                existing.CancellationReason = null;
                existing.CancellationRequestedAt = null;
                existing.CancellationPreviousStatus = null;
            }

            return Task.FromResult<CancellationStoreResult?>(
                new CancellationStoreResult(CancellationStoreOutcome.Committed, existing, from));
        }
    }

    public Task<(IReadOnlyList<DeliveryRequest> Items, int Total)> ListPendingCancellationsAsync(
        int page, int pageSize, CancellationToken ct)
    {
        lock (_writeLock)
        {
            var all = _requests.Values
                .Where(r => r.Status == RequestStatus.CancellationRequested)
                .OrderBy(r => r.CancellationRequestedAt ?? DateTimeOffset.MaxValue)
                .ToArray();

            var skip = (page - 1) * pageSize;
            IReadOnlyList<DeliveryRequest> items = all
                .Skip(skip)
                .Take(pageSize)
                .ToArray();

            return Task.FromResult((items, all.Length));
        }
    }

    public Task<IReadOnlyList<DeliveryRequest>> ListJeeberCancelledAsync(
        string jeeberId, CancellationToken ct)
    {
        lock (_writeLock)
        {
            IReadOnlyList<DeliveryRequest> rows = _requests.Values
                .Where(r => string.Equals(r.JeeberId, jeeberId, StringComparison.Ordinal)
                            && string.Equals(r.CancelledBy, "jeeber", StringComparison.Ordinal)
                            && r.Status == RequestStatus.Cancelled)
                .OrderByDescending(r => r.CancellationRequestedAt ?? DateTimeOffset.MinValue)
                .ToArray();
            return Task.FromResult(rows);
        }
    }

    /// <summary>
    /// Test/export helper — returns every request currently owned by
    /// <paramref name="clientId"/>, including terminal rows. The
    /// data-export packager (T-backend-042) uses this to include the
    /// user's full order history in the GDPR export; the production
    /// Postgres store will replace it with a "WHERE client_id = ?" query.
    /// </summary>
    public IReadOnlyList<DeliveryRequest> ListForClient(string clientId)
    {
        lock (_writeLock)
        {
            return _requests.Values
                .Where(r => string.Equals(r.ClientId, clientId, StringComparison.Ordinal))
                .OrderBy(r => r.CreatedAt)
                .ToArray();
        }
    }

    /// <summary>
    /// Async owner-scoped list backing <c>GET /requests</c>. Delegates to
    /// the existing synchronous <see cref="ListForClient"/> so the
    /// snapshot semantics (write-lock, oldest-first) stay identical — this
    /// is read-only and adds no new datastore.
    /// </summary>
    public Task<IReadOnlyList<DeliveryRequest>> ListForClientAsync(string clientId, CancellationToken ct)
        => Task.FromResult(ListForClient(clientId));

    public Task<int> AnonymizeForClientAsync(string userId, string anonymizedHash, CancellationToken ct)
    {
        lock (_writeLock)
        {
            var ids = _requests
                .Where(kv => string.Equals(kv.Value.ClientId, userId, StringComparison.Ordinal)
                    && !RequestStatus.IsActive(kv.Value.Status))
                .Select(kv => kv.Key)
                .ToList();

            foreach (var id in ids)
            {
                var existing = _requests[id];
                // ClientId is init-only — rewrite the row so the original
                // user id is genuinely gone from the in-memory image.
                _requests[id] = new DeliveryRequest
                {
                    Id = existing.Id,
                    ClientId = anonymizedHash,
                    Status = existing.Status,
                    Description = existing.Description,
                    Transcription = existing.Transcription,
                    TranscriptionConfidence = existing.TranscriptionConfidence,
                    AudioUrl = existing.AudioUrl,
                    Photos = existing.Photos,
                    TierId = existing.TierId,
                    PickupLocation = existing.PickupLocation,
                    DropoffLocation = existing.DropoffLocation,
                    PickupAddress = existing.PickupAddress,
                    DropoffAddress = existing.DropoffAddress,
                    CreatedAt = existing.CreatedAt,
                    ScheduledAt = existing.ScheduledAt,
                    ActivatedAt = existing.ActivatedAt,
                    NudgedAt = existing.NudgedAt,
                    ExpiredAt = existing.ExpiredAt,
                    JeeberId = existing.JeeberId,
                    AcceptedAt = existing.AcceptedAt,
                    CancelledBy = existing.CancelledBy,
                    CancellationReason = existing.CancellationReason,
                    CancellationRequestedAt = existing.CancellationRequestedAt,
                    CancellationApprovedAt = existing.CancellationApprovedAt,
                    CancellationRejectedAt = existing.CancellationRejectedAt,
                    CancellationPreviousStatus = existing.CancellationPreviousStatus,
                    OtpAttemptCount = existing.OtpAttemptCount,
                    OtpLockedAt = existing.OtpLockedAt,
                    ClientUnreachableAt = existing.ClientUnreachableAt,
                    OtpEscalationId = existing.OtpEscalationId
                };
            }

            return Task.FromResult(ids.Count);
        }
    }

    private int CountActiveUnlocked(string clientId)
    {
        var count = 0;
        foreach (var req in _requests.Values)
        {
            if (string.Equals(req.ClientId, clientId, StringComparison.Ordinal)
                && RequestStatus.IsActive(req.Status))
            {
                count++;
            }
        }
        return count;
    }

    private int CountJeeberActiveUnlocked(string jeeberId)
    {
        var count = 0;
        foreach (var req in _requests.Values)
        {
            if (req.JeeberId is { } jid
                && string.Equals(jid, jeeberId, StringComparison.Ordinal)
                && RequestStatus.IsJeeberActive(req.Status))
            {
                count++;
            }
        }
        return count;
    }

    private DeliveryRequest BuildRequest(CreateRequestInput input) => new()
    {
        Id = string.IsNullOrWhiteSpace(input.Id) ? Guid.NewGuid().ToString() : input.Id,
        ClientId = input.ClientId,
        Status = input.ScheduledAt is null ? RequestStatus.Pending : RequestStatus.Scheduled,
        Description = input.Description,
        Transcription = input.Transcription,
        TranscriptionConfidence = input.TranscriptionConfidence,
        AudioUrl = input.AudioUrl,
        Photos = input.Photos,
        TierId = input.TierId,
        PickupLocation = input.PickupLocation,
        DropoffLocation = input.DropoffLocation,
        PickupAddress = input.PickupAddress,
        DropoffAddress = input.DropoffAddress,
        RecipientPhone = input.RecipientPhone,
        CreatedAt = _clock.GetUtcNow(),
        ScheduledAt = input.ScheduledAt
    };
}
