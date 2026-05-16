namespace JeebGateway.Kyc;

public class KycService : IKycService
{
    private readonly IKycStore _store;
    private readonly IKycDocumentStorage _docs;
    private readonly IKycLivenessChecker _liveness;
    private readonly TimeProvider _clock;

    public KycService(
        IKycStore store,
        IKycDocumentStorage docs,
        IKycLivenessChecker liveness,
        TimeProvider clock)
    {
        _store = store;
        _docs = docs;
        _liveness = liveness;
        _clock = clock;
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
}
