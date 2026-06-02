namespace JeebGateway.Disputes.V2;

/// <summary>
/// Captures the evidence bundle (GPS polyline; chat transcript removed)
/// that <see cref="IDisputeCaseService"/> attaches to every case at
/// escalate time (T-BE-028 / JEB-64 AC1).
///
/// Production wiring proxies to:
///   - <c>olivium-dev/chat-service</c> for the transcript — REMOVED with the
///     gateway chat BFF client (salehly mirror). Transcript capture is left empty
///     until chat-service exposes a generic transcript-by-participants read.
///   - <c>olivium-dev/geolocation-service</c> for the route polyline (the
///     gateway's in-process <c>ILocationStore</c> stands in for the MVP).
///
/// Both calls run under the per-call timeout enforced by the
/// orchestrator implementation so the AC6 1-second open budget holds
/// even when one upstream is slow (PO review blocker #3).
/// </summary>
public interface IDisputeEvidenceOrchestrator
{
    Task<DisputeEvidence> CaptureAsync(
        DisputeEvidenceRequest request,
        CancellationToken ct);
}

public sealed class DisputeEvidenceRequest
{
    public required string DeliveryId { get; init; }
    public required string OpenedByUserId { get; init; }
    public string? CounterpartyUserId { get; init; }
    public string? JeeberId { get; init; }
}
