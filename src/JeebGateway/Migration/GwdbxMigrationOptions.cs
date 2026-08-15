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

    // refresh-token store -> state-service KV, W1-14 (registry token: RefreshTokenStoreMode).
    public string RefreshTokenStoreMode { get; init; } = "local";

    // GDPR account-deletion -> state-service /v1/work-items, W3-05 (registry token:
    // AccountDeletionMode). The W3-11 flip moves it off "local".
    public string AccountDeletionMode { get; init; } = "local";

    // admin_escalations -> delivery-service /api/v1/escalations (registry token:
    // OtpEscalationsMode). The mirror is fire-and-forget; the 423 path never waits (G-11).
    public string OtpEscalationsMode { get; init; } = "local";

    // prohibited_items + _acks + flagged_requests -> state-service config-surfaces/acks/work-items,
    // W3-03 (registry token: ProhibitedItemsMode). Freeze-import-flip: no dual-write rung is used.
    public string ProhibitedItemsMode { get; init; } = "local";

    // cms_surfaces pair -> state-service /v1/config-surfaces, W3-03 (registry token: CmsConfigMode).
    // Freeze-import-flip; the W3-11 flip moves it off "local".
    public string CmsConfigMode { get; init; } = "local";

    // jeeber_availability -> delivery-service /api/v1/providers/*, W3-04 (registry token:
    // AvailabilityMode). The gateway stays the single presence authority until W3-13 flips it.
    public string AvailabilityMode { get; init; } = "local";

    // push trio -> push-notification, W3-14 (registry token: PushDispatchMode). Only "local" and
    // "upstream-authority" are used: a dual-write rung would make the gateway a 2nd producer (D1).
    public string PushDispatchMode { get; init; } = "local";

    // users projection suspension -> user-management, W4-04 (registry token: UserModerationMode).
    // W4-07 opened the read rung; a Program.cs guard pins it below upstream-authority (W4-13).
    public string UserModerationMode { get; init; } = "local";

    // tiers catalog -> delivery-service, W4-09 (registry token: TiersMode). Freeze-import-flip:
    // only "local" and "upstream-authority" are valid; dual-write rungs are rejected at boot.
    public string TiersMode { get; init; } = "local";

    // delivery requests -> delivery-service, W5-02 (registry token: RequestsOwnerListMode).
    // Freeze-import-flip like Tiers: only "local" and "upstream-authority" are valid.
    // A dual-write rung is REFUSED at boot rather than merely unused — after the
    // GatewayPostgres seam was deleted the gateway's local leg is in-memory only, so a
    // dual-write-local-read rung would read from a store that empties on restart and would
    // manufacture the data loss it exists to prevent.
    public string RequestsOwnerListMode { get; init; } = "local";

    public GwdbxMigrationPhase AdminAudit => Read(AdminAuditMode);

    public GwdbxMigrationPhase DataExport => Read(DataExportMode);

    public GwdbxMigrationPhase NotificationOutbox => Read(NotificationOutboxMode);

    public GwdbxMigrationPhase RefreshTokenStore => Read(RefreshTokenStoreMode);

    public GwdbxMigrationPhase AccountDeletion => Read(AccountDeletionMode);

    public GwdbxMigrationPhase OtpEscalations => Read(OtpEscalationsMode);

    public GwdbxMigrationPhase ProhibitedItems => Read(ProhibitedItemsMode);

    public GwdbxMigrationPhase CmsConfig => Read(CmsConfigMode);

    public GwdbxMigrationPhase Availability => Read(AvailabilityMode);

    public GwdbxMigrationPhase PushDispatch => Read(PushDispatchMode);

    public GwdbxMigrationPhase UserModeration => Read(UserModerationMode);

    public GwdbxMigrationPhase Tiers => Read(TiersMode);

    public GwdbxMigrationPhase RequestsOwnerList => Read(RequestsOwnerListMode);

    public static string LadderValues => string.Join(", ", Ladder.Select(entry => entry.Wire));

    public static bool IsKnown(string? value) => TryRead(value, out _);

    // A10: from the read flip up, upstream SERVES READS — the domain's dependency must be
    // wired, because no local fallback is left to answer a read silently.
    public static bool RequiresUpstream(GwdbxMigrationPhase phase)
        => phase >= GwdbxMigrationPhase.DualWriteUpstreamRead;

    // Registration-time read: DI is wired before options bind. An unknown value lands on the
    // pinned default here and ValidateOnStart then refuses the boot, so nothing is picked off it.
    public static GwdbxMigrationPhase PhaseOf(string? value) => Read(value);

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
