using System;

namespace JeebGateway.JeebWallet;

/// <summary>R-M1 (G-01): the one spendable-wallet predicate, shared by the /v1/jeeb/wallet projection and WalletSufficiencyGuard.</summary>
/// <remarks>DENY-list by design (cod_* only): Type is an opaque string with no enum in either repo, so unknown/null stays spendable.</remarks>
public static class SpendableWalletTypes
{
    /// <summary>Frozen COD leg-type prefix; the only non-spendable class today.</summary>
    public const string CodTypePrefix = "cod_";

    /// <summary>True unless the type is a COD float leg. Null/blank stays spendable, so this pin moves no balance until cod_* rows exist.</summary>
    public static bool IsSpendable(string? walletType) =>
        !(walletType ?? string.Empty).Trim()
            .StartsWith(CodTypePrefix, StringComparison.OrdinalIgnoreCase);
}
