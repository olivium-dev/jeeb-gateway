namespace JeebGateway.Financials.Holds;

/// <summary>Wallet hold (two-phase initiate/abort) settings — DECISION-holds-mechanism Op 1/Op 5.
/// Money movement stays governed by CommissionCollection:Enabled; holds never capture.</summary>
public sealed class HoldOptions
{
    public const string SectionName = "Holds";

    /// <summary>Rollout/rollback switch: true = Layer B (real per-offer holds), false = Layer A
    /// (aggregate admission only). Cap, strict enumeration and serializer apply in BOTH modes.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>How often the reconciliation sweeper runs (orphan release, missing backfill).</summary>
    public int SweepIntervalSeconds { get; set; } = 300;

    /// <summary>How long a hold may outlive its offer before the sweeper treats it as an orphan —
    /// absorbs in-flight submit/accept races instead of aborting a hold mid-transition.</summary>
    public int OrphanGraceMinutes { get; set; } = 15;

    /// <summary>TTL of the durable hold-intent record (90d) — must outlive any offer, so a hold can
    /// never become unfindable while its offer is still live.</summary>
    public long IntentTtlSeconds { get; set; } = 7_776_000;

    /// <summary>TTL of the closed-intent tombstone. The state KV has no DELETE, so a released hold
    /// is overwritten with State=closed and expires shortly after.</summary>
    public int TombstoneTtlSeconds { get; set; } = 60;
}
