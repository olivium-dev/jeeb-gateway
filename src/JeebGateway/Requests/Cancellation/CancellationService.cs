namespace JeebGateway.Requests.Cancellation;

/// <summary>
/// T-backend-024 (JEEB-42) — see <see cref="ICancellationService"/>.
/// </summary>
public sealed class CancellationService : ICancellationService
{
    /// <summary>BR for Jeeber abuse-control: 3+ cancellations in 7 days
    /// triggers a 24-hour restriction.</summary>
    internal const int JeeberCancellationThreshold = 3;
    internal static readonly TimeSpan JeeberCancellationWindow = TimeSpan.FromDays(7);
    internal static readonly TimeSpan JeeberRestrictionDuration = TimeSpan.FromHours(24);

    /// <summary>States a Client may cancel from without admin approval.
    /// Anything strictly before pickup. <c>scheduled</c> is included so a
    /// future-dated delivery can be cancelled before its activation
    /// window opens.</summary>
    internal static readonly IReadOnlySet<string> ClientPrePickupStates =
        new HashSet<string>(StringComparer.Ordinal)
        {
            RequestStatus.Scheduled,
            RequestStatus.Pending,
            RequestStatus.Matched,
            RequestStatus.Accepted,
        };

    /// <summary>States a Client may cancel from after pickup — these
    /// require admin sign-off.</summary>
    internal static readonly IReadOnlySet<string> ClientPostPickupStates =
        new HashSet<string>(StringComparer.Ordinal)
        {
            RequestStatus.PickedUp,
            RequestStatus.HeadingOff,
        };

    /// <summary>States a Jeeber may cancel from. Only states where the
    /// Jeeber is bound to the row (accepted onwards). Once the row is
    /// already in admin-approval the Jeeber loses the right to cancel
    /// unilaterally.</summary>
    internal static readonly IReadOnlySet<string> JeeberCancellableStates =
        new HashSet<string>(StringComparer.Ordinal)
        {
            RequestStatus.Accepted,
            RequestStatus.PickedUp,
            RequestStatus.HeadingOff,
        };

    private readonly IRequestsStore _store;
    private readonly IJeeberRestrictionStore _restrictions;
    private readonly TimeProvider _clock;

    public CancellationService(
        IRequestsStore store,
        IJeeberRestrictionStore restrictions,
        TimeProvider clock)
    {
        _store = store;
        _restrictions = restrictions;
        _clock = clock;
    }

    public async Task<CancellationResult> CancelAsync(
        string deliveryId,
        string callerUserId,
        bool callerIsClient,
        bool callerIsJeeber,
        string? reason,
        CancellationToken ct)
    {
        var row = await _store.GetAsync(deliveryId, ct);
        if (row is null)
        {
            return new CancellationResult(
                CancellationOutcome.NotFound, null, null, null, false, null, null);
        }

        var actingAsClient = callerIsClient
            && string.Equals(row.ClientId, callerUserId, StringComparison.Ordinal);
        var actingAsJeeber = callerIsJeeber
            && row.JeeberId is { } jid
            && string.Equals(jid, callerUserId, StringComparison.Ordinal);

        if (!actingAsClient && !actingAsJeeber)
        {
            return new CancellationResult(
                CancellationOutcome.NotAuthorized, row, row.Status, null, false, null, null);
        }

        var trimmedReason = reason?.Trim();
        var now = _clock.GetUtcNow();

        // Jeeber path — mandatory reason, then 3+/7d threshold check.
        // Note: a Jeeber who is also the Client (dual-role on the same id)
        // can only happen if they accepted their own request, which BR-1
        // forbids. Treat the Jeeber branch first when the row's JeeberId
        // matches the caller — the post-pickup-Client branch would also
        // match for a self-accepted row and we'd misroute the cancel.
        if (actingAsJeeber)
        {
            if (string.IsNullOrWhiteSpace(trimmedReason))
            {
                return new CancellationResult(
                    CancellationOutcome.ReasonRequired, row, row.Status, null, false, null, null);
            }

            var storeResult = await _store.TryCancelAsync(
                deliveryId,
                JeeberCancellableStates,
                RequestStatus.Cancelled,
                cancelledBy: "jeeber",
                reason: trimmedReason,
                at: now,
                ct);

            if (storeResult is null)
            {
                return new CancellationResult(
                    CancellationOutcome.NotFound, null, null, null, false, null, null);
            }
            if (storeResult.Outcome == CancellationStoreOutcome.NotCancellable)
            {
                return new CancellationResult(
                    CancellationOutcome.NotCancellable, storeResult.Request, storeResult.PreviousStatus,
                    null, false, null, null);
            }

            // 3+/7d threshold. Counted AFTER the commit so the cancel we
            // just landed counts toward the trigger — without that, the
            // 3rd cancel in 7 days would only ever trip the 4th time.
            var jeeberId = storeResult.Request.JeeberId!;
            var rolling = await GetJeeberCancellationCountLast7DaysAsync(jeeberId, now, ct);

            var triggered = false;
            DateTimeOffset? expiresAt = null;
            if (rolling >= JeeberCancellationThreshold)
            {
                await _restrictions.ApplyAsync(jeeberId, now, JeeberRestrictionDuration, ct);
                expiresAt = now + JeeberRestrictionDuration;
                triggered = true;
            }

            return new CancellationResult(
                CancellationOutcome.CancelledByJeeber,
                storeResult.Request,
                storeResult.PreviousStatus,
                trimmedReason,
                triggered,
                expiresAt,
                rolling);
        }

        // Client path — pre-pickup is free, post-pickup goes to admin queue.
        if (actingAsClient)
        {
            if (ClientPrePickupStates.Contains(row.Status))
            {
                var storeResult = await _store.TryCancelAsync(
                    deliveryId,
                    ClientPrePickupStates,
                    RequestStatus.Cancelled,
                    cancelledBy: "client",
                    reason: trimmedReason,
                    at: now,
                    ct);

                if (storeResult is null)
                {
                    return new CancellationResult(
                        CancellationOutcome.NotFound, null, null, null, false, null, null);
                }
                if (storeResult.Outcome == CancellationStoreOutcome.NotCancellable)
                {
                    return new CancellationResult(
                        CancellationOutcome.NotCancellable, storeResult.Request, storeResult.PreviousStatus,
                        null, false, null, null);
                }

                return new CancellationResult(
                    CancellationOutcome.CancelledImmediately,
                    storeResult.Request,
                    storeResult.PreviousStatus,
                    trimmedReason,
                    false,
                    null,
                    null);
            }

            if (ClientPostPickupStates.Contains(row.Status))
            {
                var storeResult = await _store.TryCancelAsync(
                    deliveryId,
                    ClientPostPickupStates,
                    RequestStatus.CancellationRequested,
                    cancelledBy: "client",
                    reason: trimmedReason,
                    at: now,
                    ct);

                if (storeResult is null)
                {
                    return new CancellationResult(
                        CancellationOutcome.NotFound, null, null, null, false, null, null);
                }
                if (storeResult.Outcome == CancellationStoreOutcome.NotCancellable)
                {
                    return new CancellationResult(
                        CancellationOutcome.NotCancellable, storeResult.Request, storeResult.PreviousStatus,
                        null, false, null, null);
                }

                return new CancellationResult(
                    CancellationOutcome.PendingAdminApproval,
                    storeResult.Request,
                    storeResult.PreviousStatus,
                    trimmedReason,
                    false,
                    null,
                    null);
            }

            // Any other state — terminal or admin-pending — is uncancellable
            // by the Client.
            return new CancellationResult(
                CancellationOutcome.NotCancellable, row, row.Status, null, false, null, null);
        }

        // Defensive: unreachable, both flags must be false here.
        return new CancellationResult(
            CancellationOutcome.NotAuthorized, row, row.Status, null, false, null, null);
    }

    public async Task<(IReadOnlyList<DeliveryRequest> Items, int Total)> ListPendingApprovalsAsync(
        int page, int pageSize, CancellationToken ct)
    {
        return await _store.ListPendingCancellationsAsync(page, pageSize, ct);
    }

    public async Task<AdminCancellationDecisionResult> DecideAsync(
        string deliveryId, string action, CancellationToken ct)
    {
        bool approve;
        if (string.Equals(action, "approve", StringComparison.OrdinalIgnoreCase))
        {
            approve = true;
        }
        else if (string.Equals(action, "reject", StringComparison.OrdinalIgnoreCase))
        {
            approve = false;
        }
        else
        {
            return new AdminCancellationDecisionResult(
                AdminCancellationDecisionOutcome.UnknownAction, null, null);
        }

        var result = await _store.TryDecideCancellationAsync(deliveryId, approve, _clock.GetUtcNow(), ct);
        if (result is null)
        {
            // Either not found or the row is no longer in cancellation_requested.
            // Distinguish for the controller: a fresh GetAsync tells us which.
            var existing = await _store.GetAsync(deliveryId, ct);
            if (existing is null)
            {
                return new AdminCancellationDecisionResult(
                    AdminCancellationDecisionOutcome.NotFound, null, null);
            }
            return new AdminCancellationDecisionResult(
                AdminCancellationDecisionOutcome.NotPending, existing, existing.Status);
        }

        return new AdminCancellationDecisionResult(
            approve ? AdminCancellationDecisionOutcome.Approved : AdminCancellationDecisionOutcome.Rejected,
            result.Request,
            result.PreviousStatus);
    }

    public async Task<int> GetJeeberCancellationCountAsync(string jeeberId, CancellationToken ct)
    {
        var rows = await _store.ListJeeberCancelledAsync(jeeberId, ct);
        return rows.Count;
    }

    public async Task<int> GetJeeberCancellationCountLast7DaysAsync(
        string jeeberId, DateTimeOffset at, CancellationToken ct)
    {
        var since = at - JeeberCancellationWindow;
        var rows = await _store.ListJeeberCancelledAsync(jeeberId, ct);
        var count = 0;
        foreach (var r in rows)
        {
            if (r.CancellationRequestedAt is { } when_ && when_ >= since && when_ <= at)
            {
                count++;
            }
        }
        return count;
    }
}
