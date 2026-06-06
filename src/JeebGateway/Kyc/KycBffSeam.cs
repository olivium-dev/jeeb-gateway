using JeebGateway.Services;
using JeebGateway.Services.Clients;
using Microsoft.Extensions.Options;

namespace JeebGateway.Kyc;

/// <summary>
/// <see cref="IKycBffSeam"/> implementation. Routes the S03 JSON KYC flow
/// (submit-by-ref, ToS-stamp, queue, review-with-grant-intent) to the OWNING
/// <c>kyc-service</c> via <see cref="IKycServiceClient"/> when
/// <c>FeatureFlags:UseUpstream:Kyc</c> is ON.
///
/// <para>
/// <b>ARCH LAW (ADR-0004 / guardrail #3).</b> The gateway holds ZERO KYC business
/// state. The legacy in-gateway KYC domain (InMemoryKycStore + the document /
/// liveness fakes + the in-gateway KycService role-grant) and the interim ref
/// store have been DELETED. There is therefore no in-memory fallback to serve a
/// false-PASS off of: when the flag is OFF this seam FAILS CLOSED with a typed
/// <see cref="KycUpstreamDisabledException"/> (surfaced as 503 ProblemDetails by
/// the controllers) rather than fabricating an answer. All idempotency /
/// state-machine / role-grant-decision semantics live in kyc-service; the gateway
/// only proxies and composes (and on approve appends the role in user-management).
/// </para>
/// </summary>
public sealed class KycBffSeam : IKycBffSeam
{
    private readonly IKycServiceClient _upstream;
    private readonly IOptionsMonitor<UpstreamFeatureFlags> _flags;
    private readonly ILogger<KycBffSeam> _log;

    public KycBffSeam(
        IKycServiceClient upstream,
        IOptionsMonitor<UpstreamFeatureFlags> flags,
        ILogger<KycBffSeam> log)
    {
        _upstream = upstream;
        _flags = flags;
        _log = log;
    }

    public bool UpstreamEnabled => _flags.CurrentValue.Kyc;

    private void EnsureUpstream()
    {
        if (!UpstreamEnabled)
        {
            // Fail closed — no in-gateway KYC state exists to serve a fallback.
            throw new KycUpstreamDisabledException();
        }
    }

    public async Task<KycBffSubmitResult> SubmitByRefAsync(
        KycBffSubmitInput input,
        string idempotencyKey,
        CancellationToken ct)
    {
        EnsureUpstream();

        var result = await _upstream.SubmitAsync(new KycSubmitUpstreamPayload
        {
            UserId = input.UserId,
            IdType = input.IdType,
            IdNumber = input.IdNumber,
            IdDocumentFrontRef = input.IdDocumentFrontRef,
            IdDocumentBackRef = input.IdDocumentBackRef,
            DriverLicenseNumber = input.DriverLicenseNumber,
            DriverLicenseExpiry = input.DriverLicenseExpiry,
            VehicleRegistrationRef = input.VehicleRegistrationRef,
            VehiclePlateNumber = input.VehiclePlateNumber,
            VehicleYearMakeModel = input.VehicleYearMakeModel,
            SelfieWithLivenessRef = input.SelfieWithLivenessRef,
            TosAcceptedVersion = input.TosAcceptedVersion,
        }, idempotencyKey, ct);

        return new KycBffSubmitResult
        {
            SubmissionId = result.SubmissionId,
            State = result.State,
            TosSignedAt = result.TosSignedAt,
            TosAcceptedVersion = result.TosAcceptedVersion,
            Replayed = result.Replayed,
        };
    }

    public async Task<KycBffTosStampResult> StampTosAsync(
        string userId,
        string tosAcceptedVersion,
        string? signatureProofRef,
        string idempotencyKey,
        CancellationToken ct)
    {
        EnsureUpstream();

        // Stamp onto the caller's latest submission. If there is no submission yet
        // (ToS signed before the package is assembled), the upstream records a
        // standalone acceptance keyed by the idempotency key.
        var latest = await _upstream.GetLatestForUserAsync(userId, ct);
        var submissionId = latest?.SubmissionId ?? idempotencyKey;

        var result = await _upstream.StampTosSignatureAsync(submissionId, new KycTosStampPayload
        {
            TosAcceptedVersion = tosAcceptedVersion,
            SignatureProofRef = signatureProofRef,
        }, idempotencyKey, ct);

        return new KycBffTosStampResult
        {
            TosSignedAt = result.TosSignedAt,
            TosAcceptedVersion = result.TosAcceptedVersion,
            Replayed = result.Replayed,
        };
    }

    public async Task<KycBffSubmissionView?> GetLatestForUserAsync(string userId, CancellationToken ct)
    {
        EnsureUpstream();

        var view = await _upstream.GetLatestForUserAsync(userId, ct);
        if (view is null) return null;

        return new KycBffSubmissionView
        {
            SubmissionId = view.SubmissionId,
            UserId = view.UserId,
            Status = view.Status,
            SubmittedAt = view.SubmittedAt,
            ReviewedAt = view.ReviewedAt,
            RejectionReason = view.RejectionReason,
            TosSignedAt = view.TosSignedAt,
            TosAcceptedVersion = view.TosAcceptedVersion,
            ResubmitSteps = view.ResubmitSteps,
        };
    }

    public async Task<KycBffQueuePage> GetPendingQueueAsync(int page, int pageSize, CancellationToken ct)
    {
        EnsureUpstream();

        var queue = await _upstream.GetPendingQueueAsync(page, pageSize, ct);
        return new KycBffQueuePage
        {
            Items = queue.Items.Select(i => new KycBffQueueItem
            {
                SubmissionId = i.SubmissionId,
                UserId = i.UserId,
                Status = i.Status,
                SubmittedAt = i.SubmittedAt,
            }).ToList(),
            Page = queue.Page,
            PageSize = queue.PageSize,
            Total = queue.Total,
        };
    }

    public async Task<KycBffReviewResult> ReviewAsync(
        string submissionId,
        KycBffReviewInput input,
        CancellationToken ct)
    {
        EnsureUpstream();

        try
        {
            var decision = await _upstream.ReviewAsync(submissionId, new KycReviewDecisionRequest
            {
                Action = ToUpstreamAction(input.Action),
                ReviewerId = input.ReviewerId,
                Reason = input.Reason,
                ResubmitSteps = input.ResubmitSteps,
            }, ct);

            return new KycBffReviewResult
            {
                SubmissionId = decision.SubmissionId,
                UserId = decision.UserId,
                Status = decision.Status,
                RejectionReason = decision.RejectionReason,
                ResubmitSteps = decision.ResubmitSteps,
                GrantsRole = decision.GrantsRole,
            };
        }
        catch (KycReviewConflictException ex)
        {
            throw new KycBffReviewConflictException(ex.Message);
        }
        catch (KycReviewValidationException ex)
        {
            throw new KycBffReviewValidationException(ex.Message);
        }
    }

    private static KycReviewActionKind ToUpstreamAction(KycReviewAction action) => action switch
    {
        KycReviewAction.Approve => KycReviewActionKind.Approve,
        KycReviewAction.Reject => KycReviewActionKind.Reject,
        KycReviewAction.RequestResubmit => KycReviewActionKind.RequestResubmit,
        _ => throw new KycBffReviewValidationException($"Unsupported review action: {action}."),
    };
}

/// <summary>
/// Thrown when a KYC BFF route is hit while <c>FeatureFlags:UseUpstream:Kyc</c>
/// is OFF. The gateway holds no KYC state, so there is nothing to fall back to —
/// the controllers translate this to a 503 ProblemDetails. There is intentionally
/// no in-memory path that could serve a fabricated PASS.
/// </summary>
public sealed class KycUpstreamDisabledException : Exception
{
    public KycUpstreamDisabledException()
        : base("The KYC upstream (kyc-service) is not enabled (FeatureFlags:UseUpstream:Kyc=false).")
    {
    }
}
