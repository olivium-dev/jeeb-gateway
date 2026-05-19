using JeebGateway.Push;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace JeebGateway.Requests.Cancellation.V2;

/// <summary>
/// T-BE-030 (JEB-66) implementation of <see cref="ICancellationPolicyService"/>.
/// See <see cref="ICancellationPolicyService"/> for the orchestrator contract.
///
/// Boundary policy (AC6): the row's status is "too late to cancel" once it
/// is strictly past <see cref="RequestStatus.PickedUp"/>. Pre-pickup states
/// AND <c>picked_up</c> itself remain cancellable — Q-OPEN-2 ruling:
/// the boundary is the "Jeeber is on the road" transition (heading_off),
/// not the pickup itself, so a client who realises the parcel was put in
/// the wrong bag the second after pickup still has a window to cancel.
///
/// Fee discipline (AC1): the (soft+1)th cancellation in the ISO-week is
/// the first one that pays. The cancel is committed first; the fee post
/// is then attempted with a delivery-scoped idempotency key so a retry
/// inside the Polly pipeline cannot double-bill. Fee posting failures
/// are logged but do not roll back the cancel — the policy never sacrifices
/// a cancel for a fee.
///
/// Strike discipline (AC3): every Jeeber cancel issues a strike. When the
/// strike count inside the rolling 30-day window reaches the threshold
/// (default 3), the user-management jeeber-role suspension is applied
/// (default 7 days) and a notification is pushed. Strike count includes
/// the cancel that just landed — without that, the 3rd cancel would only
/// trip the 4th time.
/// </summary>
public sealed class CancellationPolicyService : ICancellationPolicyService
{
    /// <summary>States from which a v1 cancel is allowed. The boundary
    /// excludes <see cref="RequestStatus.HeadingOff"/> and everything
    /// terminal beyond it — those map to <see cref="CancellationPolicyOutcome.TooLateToCancel"/>.</summary>
    internal static readonly IReadOnlySet<string> CancellableStates =
        new HashSet<string>(StringComparer.Ordinal)
        {
            RequestStatus.Scheduled,
            RequestStatus.Pending,
            RequestStatus.Matched,
            RequestStatus.Accepted,
            RequestStatus.PickedUp,
        };

    /// <summary>States we treat as "too late" instead of "not cancellable".
    /// Distinct from terminal/cancelled/expired because the user is past
    /// the policy window, not in a logically invalid state.</summary>
    internal static readonly IReadOnlySet<string> TooLateStates =
        new HashSet<string>(StringComparer.Ordinal)
        {
            RequestStatus.HeadingOff,
            RequestStatus.Delivered,
            RequestStatus.Rated,
        };

    private readonly IRequestsStore _store;
    private readonly ICancellationLogStore _log;
    private readonly IUnifiedPaymentGatewayCancellationClient _payments;
    private readonly IJeeberRoleSuspensionClient _suspensions;
    private readonly IPushNotificationService _push;
    private readonly TimeProvider _clock;
    private readonly IOptions<CancellationPolicyOptions> _options;
    private readonly ILogger<CancellationPolicyService> _logger;

    public CancellationPolicyService(
        IRequestsStore store,
        ICancellationLogStore log,
        IUnifiedPaymentGatewayCancellationClient payments,
        IJeeberRoleSuspensionClient suspensions,
        IPushNotificationService push,
        TimeProvider clock,
        IOptions<CancellationPolicyOptions> options,
        ILogger<CancellationPolicyService> logger)
    {
        _store = store;
        _log = log;
        _payments = payments;
        _suspensions = suspensions;
        _push = push;
        _clock = clock;
        _options = options;
        _logger = logger;
    }

    public async Task<CancellationPolicyResult> ApplyAsync(
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
            return Empty(CancellationPolicyOutcome.NotFound);
        }

        var actingAsClient = callerIsClient
            && string.Equals(row.ClientId, callerUserId, StringComparison.Ordinal);
        var actingAsJeeber = callerIsJeeber
            && row.JeeberId is { } jid
            && string.Equals(jid, callerUserId, StringComparison.Ordinal);

        if (!actingAsClient && !actingAsJeeber)
        {
            return new CancellationPolicyResult(
                CancellationPolicyOutcome.NotAuthorized, row, row.Status, null,
                false, 0m, null, null, null, null, false, null, null, null, null);
        }

        // AC6 — strict status > picked window guard. Applies to BOTH the
        // client and the jeeber path so a jeeber who is mid-handover can't
        // dump the delivery either.
        if (TooLateStates.Contains(row.Status))
        {
            return new CancellationPolicyResult(
                CancellationPolicyOutcome.TooLateToCancel, row, row.Status, null,
                false, 0m, null, null, null, null, false, null, null, null, null);
        }

        if (!CancellableStates.Contains(row.Status))
        {
            // Terminal or admin-pending — distinct from "too late" so the
            // controller can render the right copy.
            return new CancellationPolicyResult(
                CancellationPolicyOutcome.NotCancellable, row, row.Status, null,
                false, 0m, null, null, null, null, false, null, null, null, null);
        }

        var now = _clock.GetUtcNow();
        var trimmedReason = reason?.Trim();
        var opts = _options.Value;

        return actingAsJeeber
            ? await ApplyJeeberAsync(row, callerUserId, trimmedReason, now, opts, ct)
            : await ApplyClientAsync(row, callerUserId, trimmedReason, now, opts, ct);
    }

    private async Task<CancellationPolicyResult> ApplyClientAsync(
        DeliveryRequest row,
        string clientId,
        string? reason,
        DateTimeOffset now,
        CancellationPolicyOptions opts,
        CancellationToken ct)
    {
        // AC2 — hard limit gate runs BEFORE the cancel commits so a
        // blocked attempt does not appear in the log (otherwise the count
        // grows unbounded against a user pounding the endpoint).
        var alreadyThisWeek = await _log.CountClientCancellationsInWeekAsync(clientId, now, ct);
        if (alreadyThisWeek >= opts.ClientHardLimitPerWeek)
        {
            var (_, end) = InMemoryCancellationLogStore.IsoWeekBoundsUtc(now);
            var retryAfter = (int)Math.Ceiling((end - now).TotalSeconds);

            EmitPolicyAppliedLog(
                clientId, CancellationRoles.Client, row.Id,
                count: alreadyThisWeek, fee: 0m, action: "rate_limited",
                strikeIssued: false, suspended: false);

            return new CancellationPolicyResult(
                CancellationPolicyOutcome.RateLimited, row, row.Status, reason,
                false, 0m, null, null, alreadyThisWeek, null, false, null,
                end, opts.ClientHardLimitPerWeek, alreadyThisWeek);
        }

        var storeResult = await _store.TryCancelAsync(
            row.Id,
            CancellableStates,
            RequestStatus.Cancelled,
            cancelledBy: CancellationRoles.Client,
            reason: reason,
            at: now,
            ct);

        if (storeResult is null)
        {
            return Empty(CancellationPolicyOutcome.NotFound);
        }
        if (storeResult.Outcome == CancellationStoreOutcome.NotCancellable)
        {
            return new CancellationPolicyResult(
                CancellationPolicyOutcome.NotCancellable, storeResult.Request, storeResult.PreviousStatus, reason,
                false, 0m, null, null, null, null, false, null, null, null, null);
        }

        var willBeCount = alreadyThisWeek + 1;
        var feeApplies = willBeCount > opts.ClientSoftLimitPerWeek;
        var feeAmount = feeApplies ? opts.ClientCancellationFeeLbp : 0m;
        var idempotencyKey = feeApplies ? BuildFeeIdempotencyKey(clientId, row.Id) : null;

        if (feeApplies)
        {
            var post = await _payments.PostCancellationFeeAsync(new CancellationFeePostRequest(
                UserId: clientId,
                DeliveryId: row.Id,
                Amount: feeAmount,
                Currency: opts.Currency,
                IdempotencyKey: idempotencyKey!,
                Reason: reason,
                At: now), ct);

            if (post.Outcome == CancellationFeePostOutcome.Failed)
            {
                // Fee post is best-effort per Q-OPEN-2; the cancel stays
                // committed. Operators get a structured log line they can
                // alert on.
                _logger.LogWarning(
                    "cancel.fee_post_failed delivery_id={DeliveryId} user_id={UserId} amount={Amount} currency={Currency} error={Error}",
                    row.Id, clientId, feeAmount, opts.Currency, post.Error);
            }
        }

        await _log.RecordAsync(new CancellationLogEntry(
            UserId: clientId,
            Role: CancellationRoles.Client,
            DeliveryId: row.Id,
            At: now,
            FeeApplied: feeApplies,
            FeeAmount: feeAmount,
            StrikeIssued: false,
            Reason: reason), ct);

        EmitPolicyAppliedLog(
            clientId, CancellationRoles.Client, row.Id,
            count: willBeCount, fee: feeAmount, action: feeApplies ? "cancel_with_fee" : "cancel_free",
            strikeIssued: false, suspended: false);

        return new CancellationPolicyResult(
            CancellationPolicyOutcome.CancelledByClient,
            storeResult.Request, storeResult.PreviousStatus, reason,
            FeeApplied: feeApplies,
            FeeAmount: feeAmount,
            FeeCurrency: feeApplies ? opts.Currency : null,
            FeeIdempotencyKey: idempotencyKey,
            ClientCancellationsThisWeek: willBeCount,
            JeeberStrikesLast30Days: null,
            JeeberRoleSuspended: false,
            SuspensionExpiresAt: null,
            RateLimitResetAt: null,
            RateLimitCap: null,
            RateLimitUsed: null);
    }

    private async Task<CancellationPolicyResult> ApplyJeeberAsync(
        DeliveryRequest row,
        string jeeberId,
        string? reason,
        DateTimeOffset now,
        CancellationPolicyOptions opts,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(reason))
        {
            return new CancellationPolicyResult(
                CancellationPolicyOutcome.ReasonRequired, row, row.Status, null,
                false, 0m, null, null, null, null, false, null, null, null, null);
        }

        var storeResult = await _store.TryCancelAsync(
            row.Id,
            CancellableStates,
            RequestStatus.Cancelled,
            cancelledBy: CancellationRoles.Jeeber,
            reason: reason,
            at: now,
            ct);

        if (storeResult is null)
        {
            return Empty(CancellationPolicyOutcome.NotFound);
        }
        if (storeResult.Outcome == CancellationStoreOutcome.NotCancellable)
        {
            return new CancellationPolicyResult(
                CancellationPolicyOutcome.NotCancellable, storeResult.Request, storeResult.PreviousStatus, reason,
                false, 0m, null, null, null, null, false, null, null, null, null);
        }

        // Strike rule: every Jeeber cancel is a strike. Record FIRST so
        // the rolling-window count includes the cancel that just landed,
        // then read back the count for the threshold check.
        await _log.RecordAsync(new CancellationLogEntry(
            UserId: jeeberId,
            Role: CancellationRoles.Jeeber,
            DeliveryId: row.Id,
            At: now,
            FeeApplied: false,
            FeeAmount: 0m,
            StrikeIssued: true,
            Reason: reason), ct);

        var strikes = await _log.CountJeeberStrikesInWindowAsync(
            jeeberId, now, opts.JeeberStrikeWindow, ct);

        var suspended = false;
        DateTimeOffset? expiresAt = null;
        if (strikes >= opts.JeeberStrikeThreshold)
        {
            var suspension = await _suspensions.SuspendAsync(
                jeeberId, now, opts.JeeberRoleSuspensionDuration,
                reason: $"jeeber.cancel.strike_threshold strikes={strikes} window_days={opts.JeeberStrikeWindow.TotalDays:F0}",
                ct);
            suspended = true;
            expiresAt = suspension.ExpiresAt;

            // Notification on suspension (system-design step 6). Failure is
            // best-effort — the suspension is the authoritative side-effect.
            try
            {
                await _push.SendAsync(new PushNotificationRequest(
                    UserId: jeeberId,
                    Trigger: NotificationTrigger.StatusChange,
                    Title: "Jeeber role temporarily suspended",
                    Body: $"Your jeeber role has been suspended until {suspension.ExpiresAt:u} after {strikes} cancellations in {opts.JeeberStrikeWindow.TotalDays:F0} days.",
                    Data: new Dictionary<string, string>
                    {
                        ["event"] = "jeeber.role_suspended",
                        ["strikes"] = strikes.ToString(),
                        ["expiresAt"] = suspension.ExpiresAt.ToString("u"),
                        ["windowDays"] = opts.JeeberStrikeWindow.TotalDays.ToString("F0"),
                    },
                    IdempotencyKey: $"{jeeberId}:jeeber.role_suspended:{suspension.ExpiresAt:u}"),
                    ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Jeeber role-suspension notification failed for user {UserId}", jeeberId);
            }
        }

        EmitPolicyAppliedLog(
            jeeberId, CancellationRoles.Jeeber, row.Id,
            count: strikes, fee: 0m,
            action: suspended ? "cancel_strike_suspended" : "cancel_strike",
            strikeIssued: true, suspended: suspended);

        return new CancellationPolicyResult(
            CancellationPolicyOutcome.CancelledByJeeber,
            storeResult.Request, storeResult.PreviousStatus, reason,
            FeeApplied: false,
            FeeAmount: 0m,
            FeeCurrency: null,
            FeeIdempotencyKey: null,
            ClientCancellationsThisWeek: null,
            JeeberStrikesLast30Days: strikes,
            JeeberRoleSuspended: suspended,
            SuspensionExpiresAt: expiresAt,
            RateLimitResetAt: null,
            RateLimitCap: null,
            RateLimitUsed: null);
    }

    private void EmitPolicyAppliedLog(
        string userId, string role, string deliveryId,
        int count, decimal fee, string action,
        bool strikeIssued, bool suspended)
    {
        // AC4 — single canonical structured event the observability sweep
        // greps for. Keep the field set frozen; mobile/QA depends on it.
        _logger.LogInformation(
            "cancel.policy_applied user_id={UserId} role={Role} delivery_id={DeliveryId} count={Count} fee={Fee} action={Action} strike_issued={StrikeIssued} suspended={Suspended}",
            userId, role, deliveryId, count, fee, action, strikeIssued, suspended);
    }

    private static string BuildFeeIdempotencyKey(string userId, string deliveryId) =>
        $"jeeb-cancel-fee:{userId}:{deliveryId}";

    private static CancellationPolicyResult Empty(CancellationPolicyOutcome outcome) =>
        new(outcome, null, null, null, false, 0m, null, null, null, null, false, null, null, null, null);
}
