namespace JeebGateway.Financials.Cod;

// gwdbx W2-05/W2-06 — COD→wallet mirror tunables. Dead config while
// FeatureFlags:CodSettlementMode sits at its shipped "local" default.
public sealed class CodWalletMirrorOptions
{
    public const string SectionName = "CodWalletMirror";

    // Sweep lower bound on settled_at. REQUIRED (ValidateOnStart) once the mode leaves "local";
    // an early instant IS the W2-06 backfill — owner-run, dry-run first.
    public string? ReplayFromUtc { get; init; }

    // W2-06 rehearsal: log every would-be post, POST nothing, stamp nothing.
    public bool DryRun { get; init; }

    public int SweepIntervalSeconds { get; init; } = 60;

    public int PageSize { get; init; } = 50;

    public bool TryParseReplayFrom(out DateTimeOffset from) =>
        DateTimeOffset.TryParse(ReplayFromUtc, null,
            System.Globalization.DateTimeStyles.AssumeUniversal
                | System.Globalization.DateTimeStyles.AdjustToUniversal, out from);
}
