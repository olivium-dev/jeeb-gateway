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
}
