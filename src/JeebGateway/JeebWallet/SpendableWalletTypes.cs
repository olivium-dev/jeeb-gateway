using System;

namespace JeebGateway.JeebWallet;

/// <summary>
/// R-M1 (G-01) — the single place that decides whether a generic wallet row is SPENDABLE
/// balance, shared by the <c>GET /v1/jeeb/wallet</c> projection and the F1
/// <see cref="JeebGateway.Financials.WalletSufficiencyGuard"/> so the two can never disagree.
///
/// <para>
/// The wallet-service <c>Type</c> is an opaque, caller-supplied string — neither repo
/// declares an enum or a "spendable" constant — so this is deliberately a DENY-list, not an
/// allow-list: an unrecognised or absent type stays spendable, exactly as today. COD legs are
/// the one non-spendable class the program has frozen; they land on dedicated <c>cod_*</c>
/// types (jeeber <c>cod_earnings</c>, <c>__SYSTEM__</c> <c>cod_commission</c>/<c>cod_insurance</c>)
/// and are float the holder cannot spend.
/// </para>
/// </summary>
public static class SpendableWalletTypes
{
    /// <summary>Frozen COD leg-type prefix; the only non-spendable class today.</summary>
    public const string CodTypePrefix = "cod_";

    /// <summary>
    /// True unless the type is a COD float leg. Null/blank stays spendable — that is
    /// today's behaviour for every provisioned wallet, and this pin must move no balance
    /// until <c>cod_*</c> legs actually exist.
    /// </summary>
    public static bool IsSpendable(string? walletType) =>
        !(walletType ?? string.Empty).Trim()
            .StartsWith(CodTypePrefix, StringComparison.OrdinalIgnoreCase);
}
