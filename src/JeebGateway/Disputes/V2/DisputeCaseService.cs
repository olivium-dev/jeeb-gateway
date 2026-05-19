using System.Diagnostics;
using JeebGateway.Push;
using JeebGateway.Requests;
using JeebGateway.Services.Clients;
using Microsoft.Extensions.Logging;

namespace JeebGateway.Disputes.V2;

/// <summary>
/// MVP <see cref="IDisputeCaseService"/>. Backs the new /escalate +
/// /admin/v1/resolve endpoints introduced in T-BE-028 / JEB-64.
///
/// Side-effects committed inside <see cref="EscalateAsync"/>:
/// <list type="bullet">
///   <item>One <see cref="DisputeCase"/> row in state <c>open</c>.</item>
///   <item>Evidence bundle (chat transcript + GPS polyline) captured via
///     <see cref="IDisputeEvidenceOrchestrator"/> under a strict
///     per-call timeout (PO blocker #3 + AC6).</item>
///   <item>Two push notifications — one to the filer, one to the
///     counter-party (AC2 dual fan-out applies on resolve; escalate
///     notifies the filer + admin queue).</item>
///   <item>Structured <c>dispute.opened</c> log line + OpenTelemetry
///     span + metric (AC5).</item>
/// </list>
///
/// Side-effects committed inside <see cref="ResolveAsync"/>:
/// <list type="bullet">
///   <item>State transition through <see cref="DisputeCaseState"/>.</item>
///   <item>When <c>decision = refund</c>, a single call to
///     <see cref="IPaymentRefundClient.RefundAsync"/> with the case id
///     as the idempotency key. A failed refund aborts the state
///     transition and surfaces <see cref="ResolveOutcome.RefundFailed"/>
///     (PO blocker #4 — no half-resolved cases).</item>
///   <item>Two push notifications — one to each delivery party (AC2).</item>
///   <item>Structured <c>dispute.resolved</c> log line + metric.</item>
/// </list>
/// </summary>
public sealed class DisputeCaseService : IDisputeCaseService
{
    public const int MaxPhotos = 3;
    public const int MaxReasonLength = 200;
    public const int MaxCommentLength = 2_000;
    public const int MaxNotesLength = 2_000;

    private static readonly string[] AllowedPhotoSchemes = { "https://", "http://", "s3://" };

    private readonly IDisputeCaseStore _store;
    private readonly IRequestsStore _deliveries;
    private readonly IDisputeEvidenceOrchestrator _evidence;
    private readonly IPaymentRefundClient _refund;
    private readonly IPushNotificationService _push;
    private readonly TimeProvider _clock;
    private readonly ILogger<DisputeCaseService> _log;

    public DisputeCaseService(
        IDisputeCaseStore store,
        IRequestsStore deliveries,
        IDisputeEvidenceOrchestrator evidence,
        IPaymentRefundClient refund,
        IPushNotificationService push,
        TimeProvider clock,
        ILogger<DisputeCaseService> log)
    {
        _store = store;
        _deliveries = deliveries;
        _evidence = evidence;
        _refund = refund;
        _push = push;
        _clock = clock;
        _log = log;
    }

    public async Task<EscalateResult> EscalateAsync(EscalateInput input, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(input);

        ValidateEscalate(input);

        // Idempotency replay: same key on the same delivery returns the
        // existing case row instead of double-opening (PO blocker #6).
        if (!string.IsNullOrEmpty(input.IdempotencyKey))
        {
            var replay = await _store.GetByIdempotencyKeyAsync(input.IdempotencyKey, ct).ConfigureAwait(false);
            if (replay is not null)
            {
                if (!string.Equals(replay.DeliveryId, input.DeliveryId, StringComparison.Ordinal)
                    || !string.Equals(replay.OpenedByUserId, input.OpenedByUserId, StringComparison.Ordinal))
                {
                    throw new DisputeCaseValidationException(
                        "Idempotency-Key collision: re-used key on a different delivery / opener.");
                }
                return new EscalateResult(EscalateOutcome.Replayed, replay);
            }
        }

        var delivery = await _deliveries.GetAsync(input.DeliveryId, ct).ConfigureAwait(false);
        if (delivery is null)
        {
            return new EscalateResult(EscalateOutcome.DeliveryNotFound, null);
        }

        // One active case per delivery — the admin queue cannot
        // accumulate duplicates from a frustrated user mashing escalate.
        var active = await _store.GetActiveForDeliveryAsync(input.DeliveryId, ct).ConfigureAwait(false);
        if (active is not null)
        {
            return new EscalateResult(EscalateOutcome.AlreadyEscalated, active);
        }

        using var span = DisputeCaseTelemetry.ActivitySource.StartActivity("dispute.case.open", ActivityKind.Internal);
        span?.SetTag("delivery.id", input.DeliveryId);
        span?.SetTag("opened_by", input.OpenedByUserId);

        var stopwatch = Stopwatch.StartNew();

        var counterpartyId = DetermineCounterparty(delivery, input.OpenedByUserId);

        var evidence = await _evidence.CaptureAsync(new DisputeEvidenceRequest
        {
            DeliveryId = input.DeliveryId,
            OpenedByUserId = input.OpenedByUserId,
            CounterpartyUserId = counterpartyId,
            JeeberId = delivery.JeeberId
        }, ct).ConfigureAwait(false);

        if (evidence.Degraded)
        {
            DisputeCaseTelemetry.EvidenceDegraded.Add(1);
        }

        var caseId = $"case_{Guid.NewGuid():N}";
        var @case = new DisputeCase
        {
            Id = caseId,
            DeliveryId = input.DeliveryId,
            OpenedByUserId = input.OpenedByUserId,
            CounterpartyUserId = counterpartyId,
            Reason = input.Reason.Trim(),
            Comment = string.IsNullOrWhiteSpace(input.Comment) ? null : input.Comment.Trim(),
            PhotoUrls = input.PhotoUrls,
            State = DisputeCaseState.Open,
            OpenedAt = _clock.GetUtcNow(),
            IdempotencyKey = input.IdempotencyKey,
            Evidence = evidence
        };

        var saved = await _store.AddAsync(@case, ct).ConfigureAwait(false);

        stopwatch.Stop();
        DisputeCaseTelemetry.OpenDurationMs.Record(stopwatch.Elapsed.TotalMilliseconds,
            new KeyValuePair<string, object?>("evidence_degraded", evidence.Degraded));
        DisputeCaseTelemetry.Opened.Add(1);
        span?.SetTag("case.id", saved.Id);
        span?.SetTag("evidence.degraded", evidence.Degraded);
        span?.SetTag("evidence.chat.messages", evidence.ChatTranscriptMessageCount);
        span?.SetTag("evidence.gps.points", evidence.GpsPolyline.Count);

        // AC5: structured log so dashboards / alerts can pivot on
        // event=dispute.opened with the caseId.
        _log.LogInformation(
            "event={Event} case_id={CaseId} delivery_id={DeliveryId} opened_by={OpenedBy} counterparty={Counterparty} elapsed_ms={ElapsedMs} evidence_degraded={Degraded} transcript_messages={TranscriptCount} gps_points={GpsPoints}",
            "dispute.opened", saved.Id, saved.DeliveryId, saved.OpenedByUserId,
            saved.CounterpartyUserId ?? "n/a", stopwatch.Elapsed.TotalMilliseconds,
            evidence.Degraded, evidence.ChatTranscriptMessageCount, evidence.GpsPolyline.Count);

        // Notify the filer + counter-party at open time. The admin queue
        // pickup uses the existing admin notification surface and is out
        // of scope for this story.
        await NotifyEscalateAsync(saved, ct).ConfigureAwait(false);

        return new EscalateResult(EscalateOutcome.Created, saved);
    }

    public Task<DisputeCase?> GetAsync(string caseId, CancellationToken ct)
        => _store.GetByIdAsync(caseId, ct);

    public Task<IReadOnlyList<DisputeCase>> ListForUserAsync(string userId, CancellationToken ct)
        => _store.ListForUserAsync(userId, ct);

    public async Task<ResolveResult> ResolveAsync(ResolveCaseInput input, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(input);

        ValidateResolve(input);

        var existing = await _store.GetByIdAsync(input.CaseId, ct).ConfigureAwait(false);
        if (existing is null)
        {
            return new ResolveResult(ResolveOutcome.NotFound, null, null);
        }

        if (!string.IsNullOrEmpty(input.IdempotencyKey)
            && string.Equals(existing.ResolveIdempotencyKey, input.IdempotencyKey, StringComparison.Ordinal))
        {
            // Same admin replaying the same key on an already-resolved
            // case → return the row, no side-effects (PO blocker #6).
            return new ResolveResult(ResolveOutcome.Replayed, existing, null);
        }

        if (DisputeCaseState.IsResolved(existing.State))
        {
            // AC3: 409 already_resolved on second resolve attempt.
            return new ResolveResult(ResolveOutcome.AlreadyResolved, existing, "already_resolved");
        }

        var targetState = input.Decision == ResolveDecision.Refund
            ? DisputeCaseState.ResolvedRefund
            : DisputeCaseState.ResolvedNoAction;

        if (!DisputeCaseState.CanTransition(existing.State, targetState))
        {
            throw new DisputeCaseConflictException(
                $"case {existing.Id} cannot transition from '{existing.State}' to '{targetState}'.");
        }

        using var span = DisputeCaseTelemetry.ActivitySource.StartActivity("dispute.case.resolve", ActivityKind.Internal);
        span?.SetTag("case.id", existing.Id);
        span?.SetTag("decision", input.Decision.ToString());

        var stopwatch = Stopwatch.StartNew();
        string? refundLedgerId = null;

        if (input.Decision == ResolveDecision.Refund)
        {
            var refund = await _refund.RefundAsync(new RefundRequest
            {
                DeliveryId = existing.DeliveryId,
                CaseId = existing.Id,
                AmountUsd = input.RefundUsd!.Value,
                Reason = existing.Reason,
                IdempotencyKey = $"dispute:{existing.Id}:refund"
            }, ct).ConfigureAwait(false);

            if (!refund.Success)
            {
                // PO blocker #4: refund failure aborts the state change
                // so the case stays open and the admin can retry.
                DisputeCaseTelemetry.RefundFailures.Add(1);
                _log.LogError(
                    "event=dispute.refund_failed case_id={CaseId} delivery_id={DeliveryId} amount_usd={Amount} reason={Reason}",
                    existing.Id, existing.DeliveryId, input.RefundUsd, refund.FailureReason);
                return new ResolveResult(ResolveOutcome.RefundFailed, existing, refund.FailureReason);
            }
            refundLedgerId = refund.LedgerEntryId;
        }

        var updated = await _store.ApplyResolutionAsync(existing.Id, new DisputeCaseResolutionPatch
        {
            State = targetState,
            ResolvedAt = _clock.GetUtcNow(),
            ResolverAdminId = input.AdminUserId,
            ResolutionNotes = string.IsNullOrWhiteSpace(input.Notes) ? null : input.Notes.Trim(),
            RefundUsd = input.Decision == ResolveDecision.Refund ? input.RefundUsd : null,
            RefundLedgerEntryId = refundLedgerId,
            ResolveIdempotencyKey = input.IdempotencyKey
        }, ct).ConfigureAwait(false);

        if (updated is null)
        {
            return new ResolveResult(ResolveOutcome.NotFound, null, null);
        }

        stopwatch.Stop();
        DisputeCaseTelemetry.ResolveDurationMs.Record(stopwatch.Elapsed.TotalMilliseconds,
            new KeyValuePair<string, object?>("decision", input.Decision.ToString()));
        DisputeCaseTelemetry.Resolved.Add(1,
            new KeyValuePair<string, object?>("decision", input.Decision.ToString()));

        _log.LogInformation(
            "event={Event} case_id={CaseId} delivery_id={DeliveryId} decision={Decision} refund_usd={RefundUsd} refund_ledger={RefundLedger} elapsed_ms={ElapsedMs}",
            "dispute.resolved", updated.Id, updated.DeliveryId, input.Decision,
            input.RefundUsd, refundLedgerId, stopwatch.Elapsed.TotalMilliseconds);

        // AC2: dual fan-out to both parties of the original delivery on
        // resolution.
        await NotifyResolveAsync(updated, ct).ConfigureAwait(false);

        return new ResolveResult(ResolveOutcome.Resolved, updated, null);
    }

    // -------------------------------------------------------------------
    // Validation
    // -------------------------------------------------------------------

    private static void ValidateEscalate(EscalateInput input)
    {
        var reason = (input.Reason ?? string.Empty).Trim();
        if (string.IsNullOrEmpty(reason))
        {
            throw new DisputeCaseValidationException("reason is required.");
        }
        if (reason.Length > MaxReasonLength)
        {
            throw new DisputeCaseValidationException(
                $"reason must be {MaxReasonLength} characters or fewer.");
        }

        if (!string.IsNullOrEmpty(input.Comment) && input.Comment.Length > MaxCommentLength)
        {
            throw new DisputeCaseValidationException(
                $"comment must be {MaxCommentLength} characters or fewer.");
        }

        if (input.PhotoUrls.Count > MaxPhotos)
        {
            throw new DisputeCaseValidationException(
                $"a maximum of {MaxPhotos} photo URLs is allowed per escalate.");
        }
        foreach (var url in input.PhotoUrls)
        {
            if (string.IsNullOrWhiteSpace(url)
                || !AllowedPhotoSchemes.Any(p => url.StartsWith(p, StringComparison.OrdinalIgnoreCase)))
            {
                throw new DisputeCaseValidationException(
                    $"photo URL '{url}' must start with https://, http://, or s3://.");
            }
        }
    }

    private static void ValidateResolve(ResolveCaseInput input)
    {
        if (string.IsNullOrWhiteSpace(input.AdminUserId))
        {
            throw new DisputeCaseValidationException("adminUserId is required.");
        }

        if (input.Decision == ResolveDecision.Refund)
        {
            if (!input.RefundUsd.HasValue || input.RefundUsd.Value <= 0)
            {
                throw new DisputeCaseValidationException(
                    "refundUsd is required and must be greater than 0 when decision=refund.");
            }
            if (input.RefundUsd.Value > 10_000m)
            {
                throw new DisputeCaseValidationException(
                    "refundUsd must be 10000 USD or less; escalate to ops for higher amounts.");
            }
        }
        else if (input.RefundUsd.HasValue && input.RefundUsd.Value > 0)
        {
            throw new DisputeCaseValidationException(
                "refundUsd must be null when decision=no_action.");
        }

        if (!string.IsNullOrEmpty(input.Notes) && input.Notes.Length > MaxNotesLength)
        {
            throw new DisputeCaseValidationException(
                $"notes must be {MaxNotesLength} characters or fewer.");
        }
    }

    // -------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------

    private static string? DetermineCounterparty(DeliveryRequest delivery, string openedBy)
    {
        // If the filer is the client, the counterparty is the assigned
        // Jeeber (when present). If the filer is the Jeeber, the
        // counterparty is the client.
        if (string.Equals(delivery.ClientId, openedBy, StringComparison.Ordinal))
        {
            return string.IsNullOrEmpty(delivery.JeeberId) ? null : delivery.JeeberId;
        }
        if (!string.IsNullOrEmpty(delivery.JeeberId)
            && string.Equals(delivery.JeeberId, openedBy, StringComparison.Ordinal))
        {
            return delivery.ClientId;
        }

        // Filer is neither party (admin-on-behalf path, not in scope for
        // T-BE-028 yet but harmless to allow).
        return string.IsNullOrEmpty(delivery.JeeberId) ? delivery.ClientId : delivery.JeeberId;
    }

    private async Task NotifyEscalateAsync(DisputeCase @case, CancellationToken ct)
    {
        await SendBestEffortAsync(BuildPush(
            @case.OpenedByUserId,
            "Dispute opened",
            "We received your dispute and a reviewer will follow up shortly.",
            @case,
            "opened"), ct).ConfigureAwait(false);

        if (!string.IsNullOrEmpty(@case.CounterpartyUserId))
        {
            await SendBestEffortAsync(BuildPush(
                @case.CounterpartyUserId,
                "Delivery escalated",
                "A dispute has been opened on a delivery you were part of — no action needed yet.",
                @case,
                "opened_counterparty"), ct).ConfigureAwait(false);
        }
    }

    private async Task NotifyResolveAsync(DisputeCase @case, CancellationToken ct)
    {
        var openerTitle = @case.State == DisputeCaseState.ResolvedRefund
            ? "Dispute resolved with refund"
            : "Dispute closed";
        var openerBody = @case.State == DisputeCaseState.ResolvedRefund
            ? $"Your dispute has been resolved. A refund of ${@case.RefundUsd:0.00} is on the way."
            : "Your dispute has been reviewed and closed.";

        await SendBestEffortAsync(BuildPush(
            @case.OpenedByUserId, openerTitle, openerBody, @case, "resolved"), ct).ConfigureAwait(false);

        if (!string.IsNullOrEmpty(@case.CounterpartyUserId))
        {
            var counterTitle = @case.State == DisputeCaseState.ResolvedRefund
                ? "Dispute resolved with refund"
                : "Dispute closed";
            var counterBody = @case.State == DisputeCaseState.ResolvedRefund
                ? $"A dispute on one of your deliveries was resolved with a ${@case.RefundUsd:0.00} refund."
                : "A dispute on one of your deliveries was reviewed and closed.";

            await SendBestEffortAsync(BuildPush(
                @case.CounterpartyUserId, counterTitle, counterBody, @case, "resolved_counterparty"),
                ct).ConfigureAwait(false);
        }
    }

    private static PushNotificationRequest BuildPush(
        string userId, string title, string body, DisputeCase @case, string subEvent)
        => new(
            userId,
            NotificationTrigger.DisputeUpdate,
            title,
            body,
            new Dictionary<string, string>
            {
                ["case_id"] = @case.Id,
                ["delivery_id"] = @case.DeliveryId,
                ["case_state"] = @case.State,
                ["sub_event"] = subEvent
            },
            IdempotencyKey: $"dispute:{@case.Id}:{subEvent}");

    private async Task SendBestEffortAsync(PushNotificationRequest request, CancellationToken ct)
    {
        try
        {
            await _push.SendAsync(request, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Push fan-out must never block the case mutation — the row
            // is already written and the user can read the latest state
            // via GET /v1/disputes/{id} on next foreground.
            _log.LogWarning(ex,
                "dispute push fan-out failed for user {UserId} case {CaseId} trigger {Trigger}",
                request.UserId, request.Data?["case_id"] ?? "?", request.Trigger);
        }
    }
}
