using JeebGateway.Push;
using JeebGateway.Users;
using Microsoft.Extensions.Logging;

namespace JeebGateway.Kyc;

public class KycService : IKycService
{
    private readonly IKycStore _store;
    private readonly IKycDocumentStorage _docs;
    private readonly IKycLivenessChecker _liveness;
    private readonly IUsersStore _users;
    private readonly IPushNotificationService _push;
    private readonly TimeProvider _clock;
    private readonly ILogger<KycService> _log;

    public KycService(
        IKycStore store,
        IKycDocumentStorage docs,
        IKycLivenessChecker liveness,
        IUsersStore users,
        IPushNotificationService push,
        TimeProvider clock,
        ILogger<KycService> log)
    {
        _store = store;
        _docs = docs;
        _liveness = liveness;
        _users = users;
        _push = push;
        _clock = clock;
        _log = log;
    }

    public async Task<KycSubmission> SubmitAsync(KycSubmissionInput input, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(input);

        // Documents go to encrypted storage before the queue entry exists,
        // so a half-written submission can never reference missing bytes.
        var idFrontId = await _docs.PutAsync(input.UserId, input.IdFront.FileName, input.IdFront.ContentType, input.IdFront.Bytes, ct);
        var idBackId = await _docs.PutAsync(input.UserId, input.IdBack.FileName, input.IdBack.ContentType, input.IdBack.Bytes, ct);
        var selfieId = await _docs.PutAsync(input.UserId, input.Selfie.FileName, input.Selfie.ContentType, input.Selfie.Bytes, ct);

        var liveness = await _liveness.CheckAsync(input.Selfie.Bytes, ct);

        var submission = new KycSubmission
        {
            Id = $"kyc_{Guid.NewGuid():N}",
            UserId = input.UserId,
            Status = KycStatus.PendingReview,
            SubmittedAt = _clock.GetUtcNow(),
            VehicleType = input.VehicleType,
            VehicleRegistration = input.VehicleRegistration,
            IdFrontDocumentId = idFrontId,
            IdBackDocumentId = idBackId,
            SelfieDocumentId = selfieId,
            LivenessPassed = liveness.Passed
        };
        return await _store.AddAsync(submission, ct);
    }

    public Task<KycSubmission?> GetLatestStatusAsync(string userId, CancellationToken ct) =>
        _store.GetLatestForUserAsync(userId, ct);

    public async Task<KycReviewOutcome?> ReviewAsync(string submissionId, KycReviewInput input, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(input);

        var existing = await _store.GetByIdAsync(submissionId, ct);
        if (existing is null) return null;

        var patch = BuildPatch(existing, input);

        var updated = await _store.ApplyReviewAsync(submissionId, patch, ct);
        if (updated is null) return null;

        var roleGranted = false;
        var pushSent = false;

        switch (input.Action)
        {
            case KycReviewAction.Approve:
                roleGranted = await UnlockJeeberRoleAsync(updated.UserId, ct);
                // AC #2 expects unlock within 5 seconds — there's a single
                // in-process call here so the SLA is satisfied trivially.
                // The push that follows is "you're verified" courtesy and
                // is best-effort; preference filtering / device absence
                // are handled inside PushNotificationService.
                pushSent = await SendApprovalPushAsync(updated, ct);
                break;

            case KycReviewAction.Reject:
                pushSent = await SendRejectionPushAsync(updated, ct);
                break;

            case KycReviewAction.RequestResubmit:
                pushSent = await SendResubmitPushAsync(updated, ct);
                break;
        }

        return new KycReviewOutcome
        {
            Submission = updated,
            RoleGranted = roleGranted,
            PushSent = pushSent
        };
    }

    private KycReviewPatch BuildPatch(KycSubmission existing, KycReviewInput input)
    {
        // The reviewer's call is final per submission — we only accept
        // mutations against rows still in the queue. Re-reviewing an
        // already-finalised row would silently overwrite a prior verdict.
        if (!string.Equals(existing.Status, KycStatus.PendingReview, StringComparison.Ordinal))
        {
            throw new KycReviewValidationException(
                $"Submission {existing.Id} is no longer pending review (current status: {existing.Status}).");
        }

        var reason = input.Reason?.Trim();

        switch (input.Action)
        {
            case KycReviewAction.Approve:
                return new KycReviewPatch
                {
                    Status = KycStatus.Verified,
                    ReviewedAt = _clock.GetUtcNow(),
                    ReviewerId = input.ReviewerId,
                    RejectionReason = null,
                    ResubmitSteps = Array.Empty<string>()
                };

            case KycReviewAction.Reject:
                if (string.IsNullOrEmpty(reason))
                {
                    throw new KycReviewValidationException("reason is required when rejecting a submission.");
                }
                return new KycReviewPatch
                {
                    Status = KycStatus.Rejected,
                    ReviewedAt = _clock.GetUtcNow(),
                    ReviewerId = input.ReviewerId,
                    RejectionReason = reason,
                    ResubmitSteps = Array.Empty<string>()
                };

            case KycReviewAction.RequestResubmit:
                if (string.IsNullOrEmpty(reason))
                {
                    throw new KycReviewValidationException("reason is required when requesting a resubmission.");
                }
                var steps = NormaliseSteps(input.ResubmitSteps);
                if (steps.Count == 0)
                {
                    throw new KycReviewValidationException(
                        "resubmitSteps must include at least one document step when requesting a resubmission.");
                }
                return new KycReviewPatch
                {
                    Status = KycStatus.ResubmitRequested,
                    ReviewedAt = _clock.GetUtcNow(),
                    ReviewerId = input.ReviewerId,
                    RejectionReason = reason,
                    ResubmitSteps = steps
                };

            default:
                throw new KycReviewValidationException($"Unsupported review action: {input.Action}.");
        }
    }

    private static IReadOnlyList<string> NormaliseSteps(IReadOnlyList<string>? steps)
    {
        if (steps is null || steps.Count == 0) return Array.Empty<string>();
        var unique = new List<string>();
        foreach (var raw in steps)
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;
            var trimmed = raw.Trim();
            if (!KycDocumentStep.All.Contains(trimmed))
            {
                throw new KycReviewValidationException(
                    $"'{trimmed}' is not a recognised document step. Allowed: {string.Join(", ", KycDocumentStep.All)}.");
            }
            if (!unique.Contains(trimmed, StringComparer.Ordinal))
            {
                unique.Add(trimmed);
            }
        }
        return unique;
    }

    private async Task<bool> UnlockJeeberRoleAsync(string userId, CancellationToken ct)
    {
        var before = await _users.GetByIdAsync(userId, ct);
        var alreadyHad = before is not null
            && before.Roles.Any(r => string.Equals(r, Roles.Jeeber, StringComparison.OrdinalIgnoreCase));

        var updated = await _users.GrantRoleAsync(userId, Roles.Jeeber, ct);
        if (updated is null)
        {
            // The user row vanished between submission and approval — log
            // and continue so the queue entry still moves to verified;
            // a follow-up admin task can reconcile the missing profile.
            _log.LogWarning(
                "kyc approve: user {UserId} not found in users store; role not granted",
                userId);
            return false;
        }

        return !alreadyHad;
    }

    private async Task<bool> SendApprovalPushAsync(KycSubmission submission, CancellationToken ct)
    {
        var request = new PushNotificationRequest(
            submission.UserId,
            NotificationTrigger.KycUpdate,
            "KYC verified",
            "You're verified — you can go online as a Jeeber now.",
            new Dictionary<string, string>
            {
                ["kyc_submission_id"] = submission.Id,
                ["kyc_status"] = submission.Status,
                ["kyc_action"] = "approved"
            },
            IdempotencyKey: $"kyc:{submission.Id}:approved");

        return await SendBestEffortAsync(request, ct);
    }

    private async Task<bool> SendRejectionPushAsync(KycSubmission submission, CancellationToken ct)
    {
        var reason = submission.RejectionReason ?? string.Empty;
        var request = new PushNotificationRequest(
            submission.UserId,
            NotificationTrigger.KycUpdate,
            "KYC rejected",
            string.IsNullOrEmpty(reason)
                ? "Your KYC submission was rejected. You can resubmit at any time."
                : $"Your KYC submission was rejected: {reason}. You can resubmit at any time.",
            new Dictionary<string, string>
            {
                ["kyc_submission_id"] = submission.Id,
                ["kyc_status"] = submission.Status,
                ["kyc_action"] = "rejected",
                ["kyc_reason"] = reason
            },
            IdempotencyKey: $"kyc:{submission.Id}:rejected");

        return await SendBestEffortAsync(request, ct);
    }

    private async Task<bool> SendResubmitPushAsync(KycSubmission submission, CancellationToken ct)
    {
        var reason = submission.RejectionReason ?? string.Empty;
        var stepsJoined = string.Join(",", submission.ResubmitSteps);

        var request = new PushNotificationRequest(
            submission.UserId,
            NotificationTrigger.KycUpdate,
            "KYC needs more info",
            string.IsNullOrEmpty(reason)
                ? "Please re-upload the requested KYC documents."
                : $"Please re-upload the requested KYC documents: {reason}",
            new Dictionary<string, string>
            {
                ["kyc_submission_id"] = submission.Id,
                ["kyc_status"] = submission.Status,
                ["kyc_action"] = "resubmit_requested",
                ["kyc_reason"] = reason,
                ["kyc_resubmit_steps"] = stepsJoined
            },
            IdempotencyKey: $"kyc:{submission.Id}:resubmit");

        return await SendBestEffortAsync(request, ct);
    }

    private async Task<bool> SendBestEffortAsync(PushNotificationRequest request, CancellationToken ct)
    {
        try
        {
            var result = await _push.SendAsync(request, ct);
            return result.Outcome == PushDeliveryOutcome.Delivered
                || result.Outcome == PushDeliveryOutcome.DeliveredOnRetry
                || result.Outcome == PushDeliveryOutcome.QueuedForRetry;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // A push failure must not block the reviewer's decision —
            // the row is already mutated and the user can read the new
            // status via GET /kyc/status on next foreground.
            _log.LogWarning(ex,
                "kyc review push fan-out failed for user {UserId} trigger {Trigger}",
                request.UserId, request.Trigger);
            return false;
        }
    }
}
