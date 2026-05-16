namespace JeebGateway.Kyc;

/// <summary>
/// Orchestrates the KYC submission flow (T-backend-004):
///   1. persist each uploaded document via <see cref="IKycDocumentStorage"/>
///      so the bytes land in encrypted storage,
///   2. run the liveness check stub on the selfie,
///   3. write the queue entry with status <c>pending_review</c> so the
///      admin moderation queue can pick it up.
/// </summary>
public interface IKycService
{
    Task<KycSubmission> SubmitAsync(KycSubmissionInput input, CancellationToken ct);

    Task<KycSubmission?> GetLatestStatusAsync(string userId, CancellationToken ct);
}

public class KycSubmissionInput
{
    public required string UserId { get; init; }
    public required string VehicleType { get; init; }
    public required string VehicleRegistration { get; init; }
    public required KycDocumentInput IdFront { get; init; }
    public required KycDocumentInput IdBack { get; init; }
    public required KycDocumentInput Selfie { get; init; }
}

public class KycDocumentInput
{
    public required string FileName { get; init; }
    public required string ContentType { get; init; }
    public required byte[] Bytes { get; init; }
}
