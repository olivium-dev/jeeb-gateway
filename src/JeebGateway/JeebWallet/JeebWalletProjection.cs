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
    private const decimal LowBalanceThreshold = 20.0m;

    /// <summary>
    /// Project the generic holder-wallets read into the Jeeb wallet balance the
    /// mobile <c>DioWalletRepository</c> parses. Only ACTIVE, SPENDABLE wallets
    /// (<see cref="SpendableWalletTypes"/>) contribute; an absent/empty holder
    /// projects to a zeroed, "empty"-affordability balance (mobile is defensive).
    /// </summary>
    public static JeebWalletBalanceResponse ProjectBalance(
        GetHolderWallets? holder,
        IEnumerable<Currency>? currencies = null,
        int? currencyId = null)
    {
        var wallets = holder?.Wallets ?? new List<service.ServiceWallet.Wallet>();
        // R-M1 (G-01): cod_* legs are COD float, never user-spendable balance.
        var active = wallets
            .Where(w => w is { IsActive: true } && SpendableWalletTypes.IsSpendable(w.Type))
            .Where(w => currencyId is null || w.CurrencyID == currencyId.Value)
            .ToList();

        // JEBV4-49 (M4): the generic wallet-service primitive exposes Amount as a
        // double (NSwag-generated client — a reusable-service boundary the gateway
        // must not change), so convert ONCE at this projection boundary and keep
        // decimal end-to-end for the Jeeb display contract. Realistic LBP balances
        // (millions) are well within double's exact-integer range, so the boundary
        // conversion is lossless; keeping the DTO decimal stops the value from
        // being re-serialized as a double with fractional artifacts.
        var available = (decimal)active.Sum(w => w.Amount);
        var currency = ResolveCurrency(active, currencies, currencyId);

        return new JeebWalletBalanceResponse
        {
            AvailableBalance = available,
            ReservedNow = 0,
            GiftCredit = 0,
            Currency = currency,
            AffordabilityState = ResolveAffordability(available),
        };
    }

    /// <summary>
    /// Derive the mobile affordability bucket from the available balance. This is a
    /// presentation derivation (which copy/CTA the hub shows), not a state mutation.
    /// </summary>
    private static string ResolveAffordability(decimal available)
    {
        if (available <= 0) return Affordability.Empty;
        if (available < LowBalanceThreshold) return Affordability.Low;
        return Affordability.Enough;
    }

    /// <summary>
    /// Resolve the numeric wallet currency through wallet-service's authoritative
    /// currency table. Ambiguous, absent, or malformed provider data stays null so a
    /// caller that needs money-movement proof can fail closed rather than fabricate a
    /// currency identity.
    /// </summary>
    private static string? ResolveCurrency(
        IReadOnlyCollection<service.ServiceWallet.Wallet> active,
        IEnumerable<Currency>? currencies,
        int? configuredCurrencyId)
    {
        var resolvedCurrencyId = configuredCurrencyId;
        if (resolvedCurrencyId is null)
        {
            var walletCurrencyIds = active.Select(wallet => wallet.CurrencyID).Distinct().ToArray();
            if (walletCurrencyIds.Length != 1) return null;
            resolvedCurrencyId = walletCurrencyIds[0];
        }

        var matches = currencies?
            .Where(currency => currency.Id == resolvedCurrencyId.Value)
            .ToArray();
        if (matches is not { Length: 1 } || string.IsNullOrWhiteSpace(matches[0].Code)) return null;

        return matches[0].Code.Trim().ToUpperInvariant();
    }
}
