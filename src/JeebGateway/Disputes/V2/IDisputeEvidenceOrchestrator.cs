namespace JeebGateway.Disputes.V2;

/// <summary>
/// Captures the evidence bundle (chat transcript snapshot + GPS polyline)
/// that <see cref="IDisputeCaseService"/> attaches to every case at
/// escalate time (T-BE-028 / JEB-64 AC1).
///
/// Production wiring proxies to:
///   - <c>olivium-dev/chat-service</c> for the transcript, read through the BFF
///     <see cref="Services.Clients.IChatServiceClient"/> (paging the generic
///     list-messages endpoint) — the gateway no longer holds a chat store.
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
