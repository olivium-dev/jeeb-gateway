using System;
using System.Collections.Generic;
using JeebGateway.Availability;

namespace JeebGateway.Financials;

/// <summary>One offer's exposure contribution: FeeDollars is the GROSS quoted fee in dollars (not
/// the commission), Status the raw lifecycle string resolved via PendingOfferStatus.IsLive.</summary>
public readonly record struct ExposureLeg(string OfferId, string RequestId, decimal FeeDollars, string Status);

/// <summary>C1-F1 — a jeeber's AGGREGATE outstanding commission over live offers. Pure and static so
/// submit, edit-raise, accept and the hold sweeper can never compute exposure differently.</summary>
public static class JeeberExposureCalculator
{
    /// <summary>Σ RequiredCommission over live legs, rounded PER LEG (two $100.25 legs = $20.06, not
    /// $20.05). Terminal legs never count; a null/blank exclusion excludes nothing.</summary>
    public static decimal SumLiveCommission(
        IEnumerable<ExposureLeg> legs,
        string? excludeOfferId = null,
        string? excludeRequestId = null)
    {
        if (legs is null)
        {
            return 0m;
        }

        var total = 0m;
        foreach (var leg in legs)
        {
            if (!PendingOfferStatus.IsLive(leg.Status))
            {
                continue;
            }

            // excludeOfferId = the leg being re-priced (edit); excludeRequestId = the request being
            // decided (accept).
            if (IsExcluded(leg.OfferId, excludeOfferId) || IsExcluded(leg.RequestId, excludeRequestId))
            {
                continue;
            }

            // Per-leg rounding, never a rounded grand total (CONTRACT §1).
            total += WalletGuardContract.RequiredCommission(leg.FeeDollars);
        }

        return total;
    }

    // Case-insensitive so an id differing only in GUID casing still matches its exclusion.
    private static bool IsExcluded(string? value, string? exclude) =>
        !string.IsNullOrWhiteSpace(exclude)
        && string.Equals(value, exclude, StringComparison.OrdinalIgnoreCase);
}
