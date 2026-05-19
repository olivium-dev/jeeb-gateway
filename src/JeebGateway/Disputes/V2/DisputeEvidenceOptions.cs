namespace JeebGateway.Disputes.V2;

public sealed class DisputeEvidenceOptions
{
    public const string SectionName = "Disputes:Evidence";

    /// <summary>
    /// Maximum messages to include in the captured chat transcript. Sized
    /// so a typical conversation (10–40 messages) fits comfortably under
    /// the AC6 100 kB / 1 s budget while still giving admins context.
    /// </summary>
    public int MaxTranscriptMessages { get; set; } = 200;

    /// <summary>
    /// Per-call timeout for the chat-service snapshot fetch (PO review
    /// blocker #3). Exceeded → degraded evidence, escalate still
    /// succeeds.
    /// </summary>
    public TimeSpan ChatFetchTimeout { get; set; } = TimeSpan.FromMilliseconds(400);

    /// <summary>
    /// Per-call timeout for the geolocation-service polyline fetch. Same
    /// degradation contract as the chat timeout.
    /// </summary>
    public TimeSpan GeoFetchTimeout { get; set; } = TimeSpan.FromMilliseconds(400);
}
