namespace JeebGateway.Migration;

// A10 — the ordered ladder every gateway->service extraction advances along. Named "Phase"
// rather than "*Mode" so the G-22 *Mode inventory arm keeps seeing config keys only.
public enum GwdbxMigrationPhase
{
    Local = 0,
    DualWriteLocalRead = 1,
    DualWriteUpstreamRead = 2,
    UpstreamAuthority = 3,
    Retired = 4,
}

// A10 — ONE mode key per §3-B domain, bound from FeatureFlags and ValidateOnStart-checked so an
// unknown ladder value fails the host loudly instead of silently degrading to local.
public sealed class GwdbxMigrationOptions
{
    public const string SectionName = "FeatureFlags";

    private static readonly (string Wire, GwdbxMigrationPhase Phase)[] Ladder =
    {
        ("local", GwdbxMigrationPhase.Local),
        ("dual-write-local-read", GwdbxMigrationPhase.DualWriteLocalRead),
        ("dual-write-upstream-read", GwdbxMigrationPhase.DualWriteUpstreamRead),
        ("upstream-authority", GwdbxMigrationPhase.UpstreamAuthority),
        ("retired", GwdbxMigrationPhase.Retired),
    };

    // admin_actions -> state-service /v1/audit-events (registry token: AdminAuditMode).
    public string AdminAuditMode { get; init; } = "local";

    // GDPR export -> state-service /v1/work-items (registry token: DataExportMode).
    public string DataExportMode { get; init; } = "local";

    // notification_dispatch_outbox -> state-service /v1/work-items (registry token:
    // NotificationOutboxMode). Drain-and-switch: only "local" and "upstream-authority" are used.
    public string NotificationOutboxMode { get; init; } = "local";

    public GwdbxMigrationPhase AdminAudit => Read(AdminAuditMode);

    public GwdbxMigrationPhase DataExport => Read(DataExportMode);

    public GwdbxMigrationPhase NotificationOutbox => Read(NotificationOutboxMode);

    public static string LadderValues => string.Join(", ", Ladder.Select(entry => entry.Wire));

    public static bool IsKnown(string? value) => TryRead(value, out _);

    private static GwdbxMigrationPhase Read(string? value) =>
        TryRead(value, out var phase) ? phase : GwdbxMigrationPhase.Local;

    private static bool TryRead(string? value, out GwdbxMigrationPhase phase)
    {
        foreach (var entry in Ladder)
        {
            if (string.Equals(entry.Wire, value?.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                phase = entry.Phase;
                return true;
            }
        }

        phase = GwdbxMigrationPhase.Local;
        return false;
    }
}
