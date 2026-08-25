namespace JeebGateway.Financials;

/// <summary>C1-F3 — the cross-request live-offer ceiling (OD-C1-1). Bounds how much exposure one
/// jeeber can open at once; the per-request one-live rule stays where it is.</summary>
public sealed class OfferLimitsOptions
{
    public const string SectionName = "Offers";

    /// <summary>Live offers (pending/accepted) a jeeber may hold across ALL requests. Default 20;
    /// int.MaxValue disables the cap without a deploy (E2 409 is returned when live >= limit).</summary>
    public int MaxLiveOffersPerJeeber { get; set; } = 20;
}
