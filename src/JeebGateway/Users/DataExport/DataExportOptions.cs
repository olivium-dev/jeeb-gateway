namespace JeebGateway.Users.DataExport;

/// <summary>
/// Tunables for the data-export pipeline (T-backend-042, GDPR-like right
/// of access). Production deployments override these via configuration;
/// the defaults encode the acceptance criteria (72-hour SLA).
/// </summary>
public class DataExportOptions
{
    public const string SectionName = "Users:DataExport";

    /// <summary>
    /// Maximum time between queueing a request and delivering the
    /// download link. AC: "Secure download link sent via email/push
    /// within 72 hours."
    /// </summary>
    public TimeSpan Sla { get; set; } = TimeSpan.FromHours(72);

    /// <summary>
    /// How long the secure download link stays usable after the export is
    /// ready. Independent from the SLA: the user has 7 days from delivery
    /// to fetch the file before the link is invalidated.
    /// </summary>
    public TimeSpan LinkValidity { get; set; } = TimeSpan.FromDays(7);

    /// <summary>
    /// Delay before an externally scheduled sweep retries an export whose
    /// owner contract cannot yet supply a complete section. No gateway
    /// background processor exists.
    /// </summary>
    public TimeSpan SourceUnavailableRetryDelay { get; set; } = TimeSpan.FromMinutes(15);
}
