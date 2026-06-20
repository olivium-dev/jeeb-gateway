using System;
using System.Collections.Generic;
using System.Linq;
using JeebGateway.service.ServiceWallet;

namespace JeebGateway.JeebWallet;

/// <summary>
/// The Jeeb-domain projection that shapes the GENERIC wallet-service primitive
/// (<see cref="GetHolderWallets"/> — opaque holder + wallet rows) into the
/// Jeeb-facing balance/summary the mobile app parses at
/// <c>GET /v1/jeeb/wallet</c>.
///
/// <para>
/// ADR-0001 (stateless &amp; thin): this is a PURE, side-effect-free MAP — no
/// state, no persistence, no I/O. All Jeeb presentation vocabulary
/// (availableBalance / reservedNow / giftCredit / affordabilityState) is applied
/// HERE over the shared opaque wallet rows; the generic wallet-service learns
/// nothing about Jeeb. Mirrors the <see cref="JeebGateway.Ratings.Jeeb"/>
/// generic→Jeeb projection pattern so it can be unit-tested without HTTP/DI.
/// </para>
/// </summary>
public static class JeebWalletProjection
{
    /// <summary>The affordability buckets the mobile wallet hub renders.</summary>
    public static class Affordability
    {
        public const string Enough = "enough";
        public const string Low = "low";
        public const string Empty = "empty";
        public const string AllReserved = "all_reserved";
    }

    /// <summary>
    /// Below this available balance the mobile hub nudges the jeeber to top up
    /// (mobile maps anything &gt; 0 and &lt; this to the "low" state). Presentation
    /// threshold only — NOT a domain rule (no money moves on it).
    /// </summary>
    private const double LowBalanceThreshold = 20.0;

    /// <summary>
    /// Project the generic holder-wallets read into the Jeeb wallet balance the
    /// mobile <c>DioWalletRepository</c> parses. Only ACTIVE wallets contribute to
    /// the available balance; an absent/empty holder projects to a zeroed,
    /// "empty"-affordability balance (mobile parses defensively either way).
    /// </summary>
    public static JeebWalletBalanceResponse ProjectBalance(GetHolderWallets? holder)
    {
        var wallets = holder?.Wallets ?? new List<service.ServiceWallet.Wallet>();
        var active = wallets.Where(w => w is { IsActive: true }).ToList();

        var available = active.Sum(w => w.Amount);
        var currency = ResolveCurrency(active);

        return new JeebWalletBalanceResponse
        {
            AvailableBalance = available,
            ReservedNow = 0,
            GiftCredit = 0,
            Currency = currency,
            AffordabilityState = ResolveAffordability(available, hasActiveWallet: active.Count > 0),
        };
    }

    /// <summary>
    /// Derive the mobile affordability bucket from the available balance and whether
    /// the holder has any active wallets. Distinguishes S-10 edge cases:
    /// <list type="bullet">
    ///   <item><c>empty</c> — no active wallets (never topped up).</item>
    ///   <item><c>all_reserved</c> — wallet exists but net balance is zero or negative
    ///     (all funds are tied up in pending transactions).</item>
    ///   <item><c>low</c> — positive balance below the top-up nudge threshold.</item>
    ///   <item><c>enough</c> — positive balance above threshold.</item>
    /// </list>
    /// </summary>
    private static string ResolveAffordability(double available, bool hasActiveWallet)
    {
        if (available <= 0 && !hasActiveWallet) return Affordability.Empty;
        if (available <= 0) return Affordability.AllReserved;
        if (available < LowBalanceThreshold) return Affordability.Low;
        return Affordability.Enough;
    }

    /// <summary>
    /// The generic wallet row carries only a numeric <c>CurrencyID</c> (the wallet
    /// service's own currency table key), not an ISO code, so the gateway cannot
    /// faithfully name it without inventing a mapping (which would be domain state
    /// the gateway must not hold). Leave it null and let the mobile parser apply its
    /// documented default — honest over fabricated.
    /// </summary>
    private static string? ResolveCurrency(IReadOnlyCollection<service.ServiceWallet.Wallet> _) => null;
}
