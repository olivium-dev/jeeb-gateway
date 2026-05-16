namespace JeebGateway.Kyc;

/// <summary>
/// POST /kyc/submit response (T-backend-004). The mobile app reads
/// <see cref="Status"/> to switch the Jeeber dashboard between "your
/// documents are under review" and "you're verified, go online".
/// </summary>
public class KycSubmissionResponse
{
    public required string Id { get; init; }
    public required string UserId { get; init; }
    public required string Status { get; init; }
    public required DateTimeOffset SubmittedAt { get; init; }
    public DateTimeOffset? ReviewedAt { get; init; }
    public string? RejectionReason { get; init; }
    public required string VehicleType { get; init; }
    public required string VehicleRegistration { get; init; }
    public required bool LivenessPassed { get; init; }

    /// <summary>
    /// T-backend-005 AC #4. Populated when status is
    /// <c>resubmit_requested</c>; lists the document steps the user
    /// must re-upload. Empty for every other status.
    /// </summary>
    public IReadOnlyList<string> ResubmitSteps { get; init; } = Array.Empty<string>();
}

/// <summary>
/// PATCH /admin/kyc/{id}/review request body (T-backend-005). The
/// reviewer picks one action; <c>reason</c> is required for reject and
/// request_resubmit, <c>resubmitSteps</c> is required for
/// request_resubmit only.
/// </summary>
public class KycReviewRequest
{
    public string? Action { get; init; }
    public string? Reason { get; init; }
    public IReadOnlyList<string>? ResubmitSteps { get; init; }
}

/// <summary>
/// PATCH /admin/kyc/{id}/review response. Mirrors the submission row
/// after the mutation plus a couple of fan-out flags so the admin UI
/// can show "role granted in N ms" / "push queued".
/// </summary>
public class KycReviewResponse
{
    public required KycSubmissionResponse Submission { get; init; }
    public required bool RoleGranted { get; init; }
    public required bool PushSent { get; init; }
}

/// <summary>
/// GET /admin/kyc/queue response. Mirrors AdminUserSearchResponse so
/// the admin UI's pager wiring is consistent across endpoints.
/// </summary>
public class KycQueueResponse
{
    public required IReadOnlyList<KycQueueItem> Items { get; init; }
    public required int Page { get; init; }
    public required int PageSize { get; init; }
    public required int Total { get; init; }
}

public class KycQueueItem
{
    public required string Id { get; init; }
    public required string UserId { get; init; }
    public required string Status { get; init; }
    public required DateTimeOffset SubmittedAt { get; init; }
    public required string VehicleType { get; init; }
    public required string VehicleRegistration { get; init; }
    public required bool LivenessPassed { get; init; }
}

/// <summary>
/// GET /kyc/status response. Wraps the latest <see cref="KycSubmissionResponse"/>
/// in an envelope so the contract can grow (history, attempt count) without
/// breaking the existing mobile client.
/// </summary>
public class KycStatusResponse
{
    public required string UserId { get; init; }
    public required bool HasSubmission { get; init; }
    public KycSubmissionResponse? Latest { get; init; }
}
