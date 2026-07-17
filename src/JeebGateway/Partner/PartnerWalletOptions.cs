using System.ComponentModel.DataAnnotations;

namespace JeebGateway.Partner;

/// <summary>
/// Bound configuration for the Jeeb Partner Portal wallet BFF (partner-wallet-bff).
///
/// <para><b>No secrets.</b> Every value here is non-sensitive presentation/routing metadata
/// (wallet-service transaction <c>ServiceName</c>/<c>Tag</c> labels and the ledger base
/// <c>CurrencyId</c>). The wallet-service base URL and any credentials live under the existing
/// <c>WalletServiceApi</c> section / environment, never here (olivium-secrets-hardening).</para>
///
/// <para>Bound + validated at startup via <c>AddOptions().BindConfiguration().ValidateDataAnnotations()
/// .ValidateOnStart()</c> so a mis-configured partner section fails the host loudly rather than at
/// first money move (dotnet-options-pattern skill).</para>
/// </summary>
public sealed class PartnerWalletOptions
{
    public const string SectionName = "PartnerWallet";

    /// <summary>
    /// The <c>ServiceName</c> stamped on wallet-service transactions this BFF initiates. Lets
    /// finance/ops attribute partner top-ups &amp; credits in the wallet ledger.
    /// </summary>
    [Required, MinLength(3), MaxLength(64)]
    public string ServiceName { get; init; } = "jeeb-partner-portal";

    /// <summary>Wallet-service transaction <c>Tag</c> for a partner→jeeber top-up move.</summary>
    [Required, MinLength(3), MaxLength(64)]
    public string TopupTag { get; init; } = "partner-topup";

    /// <summary>Wallet-service transaction <c>Tag</c> for an admin cash-credit into a partner wallet.</summary>
    [Required, MinLength(3), MaxLength(64)]
    public string CreditTag { get; init; } = "partner-cash-credit";

    /// <summary>
    /// The wallet-service currency id the partner wallet operates in. Used only to pick the
    /// holder's matching wallet among (possibly) several; NOT a money computation.
    /// </summary>
    [Range(1, int.MaxValue)]
    public int CurrencyId { get; init; } = 1;

    /// <summary>
    /// Upper bound (inclusive) the gateway rejects a single top-up/credit ABOVE with a 400 before
    /// any wallet-service call — a cheap guardrail against fat-finger amounts, NOT a fee rule. The
    /// authoritative limits (balance, fees, BR caps) remain wallet-service's.
    /// </summary>
    [Range(0.01, double.MaxValue)]
    public double MaxTransferAmount { get; init; } = 100_000d;
}
