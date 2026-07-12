namespace JeebGateway.Financials;

/// <summary>
/// JEBV4-47 (M3/R7): tuning for the <see cref="SettlementLedgerReconciler"/> that
/// replays settlement UPG ledger posts left unposted (ledger_entry_id NULL) when
/// UPG was down at settle time. Safe defaults require no appsettings change.
/// </summary>
public sealed class SettlementLedgerReconcilerOptions
{
    public const string SectionName = "Financials:SettlementLedgerReconciler";

    /// <summary>How often the reconciler scans for unposted settlement ledger rows.</summary>
    public TimeSpan SweepInterval { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Bounded page size per sweep (LIMIT). Keeps a large backlog from being pulled
    /// into memory at once; the next tick continues from the stable ordering.
    /// </summary>
    public int PageSize { get; set; } = 100;
}
