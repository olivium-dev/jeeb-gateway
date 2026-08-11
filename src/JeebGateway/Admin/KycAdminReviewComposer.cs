using JeebGateway.Kyc;
using JeebGateway.Services;
using JeebGateway.Users;
using Microsoft.Extensions.Options;

namespace JeebGateway.Admin;

/// <summary>
/// The KYC review DECISION composition, extracted verbatim from
/// <c>AdminKycController.Review</c> so the CMS-compat review route
/// (<c>PATCH /user-management/admin/kyc/{id}</c>) and the native route
/// (<c>PATCH /admin/kyc/{id}/review</c>) run the SAME code.
///
/// <para><b>Why extraction, not a second implementation.</b> This path owns CP-C / H8 — the only
/// identity-mutating transition in the gateway: on approve the seam returns the role-grant INTENT,
/// the gateway enforces the {client,jeeber} contract whitelist (JEB-1472/AC3), translates to the
/// OPAQUE user-management role (N14 — the S03 403 root-fix), appends it in user-management, and
/// appends the admin audit entry. A forked copy would drift on exactly the rule whose last
/// divergence produced a live 403.</para>
///
/// <para>The approve COMMITS regardless of the user-management leg (N14): a UM blip surfaces
/// <see cref="KycAdminReviewOutcome.RoleGranted"/> = false and logs; it never rolls the review back.</para>
/// </summary>
public sealed class KycAdminReviewComposer
{
    public const int MaxReasonLength = 500;

    private const string EntityType = "kyc_submission";

    private readonly IKycBffSeam _kyc;
    private readonly IUserManagementDualRoleClient _userManagement;
    private readonly IOptionsMonitor<UpstreamFeatureFlags> _flags;
    private readonly IAdminAuditLog _auditLog;
    private readonly ILogger<KycAdminReviewComposer> _log;

    public KycAdminReviewComposer(
        IKycBffSeam kyc,
        IUserManagementDualRoleClient userManagement,
        IOptionsMonitor<UpstreamFeatureFlags> flags,
        IAdminAuditLog auditLog,
        ILogger<KycAdminReviewComposer> log)
    {
        _kyc = kyc;
        _userManagement = userManagement;
        _flags = flags;
        _auditLog = auditLog;
        _log = log;
    }

    /// <summary>
    /// Parses the wire action token. <c>request_resubmit</c>/<c>resubmit</c> are accepted on the
    /// native route; the CMS contract only ever sends approve|reject.
    /// </summary>
    public static bool TryParseAction(string? raw, out KycReviewAction action, out string error)
    {
        action = default;
        error = string.Empty;

        if (string.IsNullOrWhiteSpace(raw))
        {
            error = "action is required (approve, reject, or request_resubmit).";
            return false;
        }

        switch (raw.Trim().ToLowerInvariant())
        {
            case "approve":
                action = KycReviewAction.Approve;
                return true;
            case "reject":
                action = KycReviewAction.Reject;
                return true;
            case "request_resubmit":
            case "resubmit":
                action = KycReviewAction.RequestResubmit;
                return true;
            default:
                error = $"Unknown action '{raw}'. Allowed: approve, reject, request_resubmit.";
                return false;
        }
    }

    /// <summary>
    /// Runs the review: seam decision → contract-role whitelist → user-management append →
    /// audit append. Every failure mode is returned as a typed
    /// <see cref="KycAdminReviewOutcome"/>; the controllers only translate it to HTTP.
    /// </summary>
    public async Task<KycAdminReviewOutcome> ReviewAsync(
        string submissionId,
        KycReviewAction action,
        string adminId,
        string? reason,
        IReadOnlyList<string>? resubmitSteps,
        string requestId,
        CancellationToken ct)
    {
        var trimmedReason = reason?.Trim();
        if (trimmedReason is { Length: > MaxReasonLength })
        {
            return KycAdminReviewOutcome.Failed(
                KycAdminReviewStatus.BadRequest,
                $"reason must be {MaxReasonLength} characters or fewer.");
        }

        KycBffReviewResult outcome;
        try
        {
            outcome = await _kyc.ReviewAsync(submissionId, new KycBffReviewInput
            {
                Action = action,
                ReviewerId = adminId,
                Reason = trimmedReason,
                ResubmitSteps = resubmitSteps
            }, ct);
        }
        catch (KycUpstreamDisabledException)
        {
            return KycAdminReviewOutcome.Failed(KycAdminReviewStatus.UpstreamDisabled, null);
        }
        catch (KycBffNotFoundException)
        {
            return KycAdminReviewOutcome.Failed(KycAdminReviewStatus.NotFound, null);
        }
        catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            // JEB-1522: the live kyc-service answers 404 for an unknown submission id, which the
            // typed client surfaces as HttpRequestException(404) rather than KycBffNotFoundException.
            return KycAdminReviewOutcome.Failed(KycAdminReviewStatus.NotFound, null);
        }
        catch (KycBffReviewConflictException ex)
        {
            return KycAdminReviewOutcome.Failed(KycAdminReviewStatus.Conflict, ex.Message);
        }
        catch (KycBffReviewValidationException ex)
        {
            return KycAdminReviewOutcome.Failed(KycAdminReviewStatus.BadRequest, ex.Message);
        }

        var roleGranted = false;
        if (!string.IsNullOrWhiteSpace(outcome.GrantsRole))
        {
            try
            {
                roleGranted = await ComposeRoleGrantAsync(
                    outcome.SubmissionId, outcome.UserId, outcome.GrantsRole!, ct);
            }
            catch (InvalidContractRoleException ex)
            {
                return KycAdminReviewOutcome.Failed(KycAdminReviewStatus.InvalidRole, ex.Message);
            }
        }

        await _auditLog.AppendAsync(new AdminAuditAppend
        {
            AdminUserId = adminId,
            Action = AuditActionFor(action),
            EntityType = EntityType,
            EntityId = submissionId,
            BeforeState = new Dictionary<string, object?> { ["status"] = "pending_review" },
            AfterState = new Dictionary<string, object?>
            {
                ["status"] = outcome.Status,
                ["rejection_reason"] = outcome.RejectionReason,
                ["resubmit_steps"] = outcome.ResubmitSteps.ToList(),
                ["role_granted"] = roleGranted
            },
            RequestId = requestId
        }, ct);

        return KycAdminReviewOutcome.Ok(outcome, roleGranted);
    }

    /// <summary>
    /// CP-C / H8 role-grant composition. Enforces the gateway-owned {client,jeeber} contract
    /// whitelist BEFORE any grant (JEB-1472/AC3 → invalid_role 400), translating to the OPAQUE
    /// user-management role on the way (N14: UM stores driver/customer and the role switch looks
    /// up the opaque name, so appending the literal 'jeeber' produced a live 403).
    /// </summary>
    private async Task<bool> ComposeRoleGrantAsync(
        string submissionId, string? subjectUserId, string contractRole, CancellationToken ct)
    {
        var opaqueRole = JeebRoleTranslator.ToOpaque(contractRole);
        if (opaqueRole is null)
        {
            _log.LogWarning(
                "kyc approve {SubmissionId}: grant role '{Role}' is not a recognised Jeeb contract role; "
                + "rejecting as invalid_role", submissionId, contractRole);
            throw new InvalidContractRoleException(contractRole);
        }

        if (!_flags.CurrentValue.UserManagement)
        {
            // Interim path: the in-gateway service already appended the role.
            return true;
        }

        if (string.IsNullOrWhiteSpace(subjectUserId))
        {
            _log.LogWarning(
                "kyc approve {SubmissionId}: review outcome carried no owner; role grant skipped", submissionId);
            return false;
        }

        try
        {
            var grant = await _userManagement.AppendAvailableRoleAsync(subjectUserId, opaqueRole, ct);
            return grant.Added || grant.AvailableRoles.Any(
                r => string.Equals(r, opaqueRole, StringComparison.OrdinalIgnoreCase));
        }
        catch (UserManagementCallException ex)
        {
            // Approve never rolls back on a UM blip (N14); surface false + log.
            _log.LogWarning(ex,
                "kyc approve {SubmissionId}: user-management role append failed (status {Status}); "
                + "approve committed, role grant deferred", submissionId, ex.StatusCode);
            return false;
        }
    }

    private static string AuditActionFor(KycReviewAction action) => action switch
    {
        KycReviewAction.Approve => "approve_kyc",
        KycReviewAction.Reject => "reject_kyc",
        KycReviewAction.RequestResubmit => "request_resubmit_kyc",
        _ => action.ToString()
    };
}

/// <summary>Typed review result; the controllers map each status to their declared HTTP shape.</summary>
public enum KycAdminReviewStatus
{
    Ok,
    BadRequest,
    NotFound,
    Conflict,
    UpstreamDisabled,
    InvalidRole,
}

/// <inheritdoc cref="KycAdminReviewStatus"/>
public sealed record KycAdminReviewOutcome(
    KycAdminReviewStatus Status,
    KycBffReviewResult? Result,
    bool RoleGranted,
    string? Error)
{
    public static KycAdminReviewOutcome Ok(KycBffReviewResult result, bool roleGranted)
        => new(KycAdminReviewStatus.Ok, result, roleGranted, null);

    public static KycAdminReviewOutcome Failed(KycAdminReviewStatus status, string? error)
        => new(status, null, false, error);
}
