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
