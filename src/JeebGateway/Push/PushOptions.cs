namespace JeebGateway.Push;

/// <summary>
/// Configuration for the push pipeline (T-backend-022). Defaults match the
/// ticket's acceptance criteria: 5-second delivery SLA, single retry 30
/// seconds after a failed first attempt.
///
/// <para><b>b05/GW1 W0.6.</b> The direct-to-Google transport class, its
/// <c>UseFcmTransport</c> switch and its two credential-shaped options
/// (project id + bearer token) were DELETED, not disabled. Owner ruling: the
/// gateway must never speak to a push provider itself — every push leaves via
/// the push microservice (:10040). Do not re-add a credential property here.</para>
/// </summary>
public sealed class PushOptions
{
    public const string SectionName = "Push";

    /// <summary>AC: "Delivery within 5 seconds of trigger".</summary>
    public TimeSpan DeliverySla { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>AC: "Failed notifications retried once after 30 seconds".</summary>
    public TimeSpan RetryDelay { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Per-transport HTTP timeout. Tight to keep the 5s SLA.</summary>
    public TimeSpan TransportTimeout { get; set; } = TimeSpan.FromSeconds(2);
}
