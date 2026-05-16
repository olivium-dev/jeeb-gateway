namespace JeebGateway.Push;

/// <summary>
/// Configuration for the push pipeline (T-backend-022). Defaults match the
/// ticket's acceptance criteria: 5-second delivery SLA, single retry 30
/// seconds after a failed first attempt.
/// </summary>
public sealed class PushOptions
{
    public const string SectionName = "Push";

    /// <summary>AC: "Delivery within 5 seconds of trigger".</summary>
    public TimeSpan DeliverySla { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>AC: "Failed notifications retried once after 30 seconds".</summary>
    public TimeSpan RetryDelay { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>How often the retry queue is polled by <see cref="PushRetryQueueProcessor"/>.</summary>
    public TimeSpan RetryQueueScanInterval { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>Per-transport HTTP timeout. Tight to keep the 5s SLA.</summary>
    public TimeSpan TransportTimeout { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>Google Cloud project ID for FCM HTTP v1 API.</summary>
    public string FcmProjectId { get; set; } = string.Empty;

    /// <summary>
    /// OAuth2 bearer token for FCM HTTP v1. In production, rotate via
    /// Google Application Default Credentials; for the MVP a long-lived
    /// service account token is acceptable.
    /// </summary>
    public string FcmBearerToken { get; set; } = string.Empty;

    /// <summary>Whether to use the real FCM transport or the in-memory test double.</summary>
    public bool UseFcmTransport { get; set; }
}
