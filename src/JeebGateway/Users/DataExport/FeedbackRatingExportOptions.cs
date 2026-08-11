namespace JeebGateway.Users.DataExport;

/// <summary>
/// Configuration for the GDPR-export ratings consumer. The base URL is the existing
/// <c>FeedbackServiceApi:BaseUrl</c>; only the credential and the page bound live here.
/// Unset <see cref="ServiceTokenFile"/> keeps the in-memory provider bound, so the
/// wiring is additive and CI/dev need no secret.
/// </summary>
public sealed class FeedbackRatingExportOptions
{
    public const string SectionName = "Users:DataExport:FeedbackRatings";

    /// <summary>
    /// Absolute path to the mounted shared-secret file holding the value
    /// feedback-service reads from <c>FEEDBACK_EXPORT_TOKEN_FILE</c>. Sent as
    /// <c>X-Feedback-Service-Token</c>. Env key
    /// <c>Users__DataExport__FeedbackRatings__ServiceTokenFile</c>.
    /// </summary>
    public string? ServiceTokenFile { get; init; }

    /// <summary>
    /// Kill switch. False keeps the in-memory provider bound even when a token
    /// file is configured, so the consumer can be disabled without unmounting
    /// the secret.
    /// </summary>
    public bool Enabled { get; init; } = true;

    /// <summary>
    /// Rows requested per export. Upstream caps this at 100 and offers no
    /// cursor (keyset paging is deferred until its Guid tie-break is proven on
    /// real Postgres), so an over-100 history is truncated and logged.
    /// </summary>
    public int PageLimit { get; init; } = 100;

    /// <summary>Per-call timeout; a feedback blip must not stall the packager.</summary>
    public int TimeoutSeconds { get; init; } = 10;

    public bool IsConfigured =>
        Enabled && !string.IsNullOrWhiteSpace(ServiceTokenFile);
}
