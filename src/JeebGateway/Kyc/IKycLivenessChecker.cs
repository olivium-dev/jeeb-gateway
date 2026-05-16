namespace JeebGateway.Kyc;

/// <summary>
/// Liveness-detection seam invoked during KYC submission (T-backend-004).
/// Production wiring will call an external face-liveness vendor; the MVP
/// stub returns a deterministic pass for any non-empty selfie so the
/// queue entry can still be created without external dependencies.
/// </summary>
public interface IKycLivenessChecker
{
    Task<LivenessCheckResult> CheckAsync(byte[] selfieBytes, CancellationToken ct);
}

public class LivenessCheckResult
{
    public required bool Passed { get; init; }
    public required decimal Confidence { get; init; }
    public string? Reason { get; init; }
}

/// <summary>
/// Stub implementation: passes any selfie that is at least one byte long.
/// Empty payloads are caught earlier in the controller layer, so this
/// branch only fires in unit tests that bypass the controller.
/// </summary>
public class StubKycLivenessChecker : IKycLivenessChecker
{
    public Task<LivenessCheckResult> CheckAsync(byte[] selfieBytes, CancellationToken ct)
    {
        var passed = selfieBytes is { Length: > 0 };
        return Task.FromResult(new LivenessCheckResult
        {
            Passed = passed,
            Confidence = passed ? 0.95m : 0m,
            Reason = passed ? null : "selfie payload was empty"
        });
    }
}
