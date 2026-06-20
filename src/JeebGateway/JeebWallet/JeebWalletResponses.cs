using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace JeebGateway.JeebWallet;

/// <summary>
/// The Jeeb wallet BALANCE/summary body for <c>GET /v1/jeeb/wallet</c>. Field
/// names are the exact camelCase keys the mobile <c>DioWalletRepository._parse</c>
/// reads (it also tolerates snake_case + null, but the gateway emits the canonical
/// camelCase shape).
/// </summary>
public sealed class JeebWalletBalanceResponse
{
    [JsonPropertyName("availableBalance")]
    public double AvailableBalance { get; set; }

    [JsonPropertyName("reservedNow")]
    public double ReservedNow { get; set; }

    [JsonPropertyName("giftCredit")]
    public double GiftCredit { get; set; }

    /// <summary>One of enough | low | empty | all_reserved (mobile enum).</summary>
    [JsonPropertyName("affordabilityState")]
    public string AffordabilityState { get; set; } = JeebWalletProjection.Affordability.Empty;

    /// <summary>
    /// ISO currency code, or null when the generic wallet row cannot supply one
    /// (mobile applies its own default). Omitted from the payload when null.
    /// </summary>
    [JsonPropertyName("currency")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Currency { get; set; }
}

/// <summary>
/// One ledger entry in <c>GET /v1/jeeb/wallet/ledger</c>. Keys match
/// <c>DioWalletLedgerRepository._entry</c> (id, type, amount, sign, ref, ts, currency).
/// </summary>
public sealed class JeebWalletLedgerEntry
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    /// <summary>reserve | fee_won | released | refund | penalty | topup | gift.</summary>
    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty;

    [JsonPropertyName("amount")]
    public double Amount { get; set; }

    /// <summary>+1 credit / -1 debit.</summary>
    [JsonPropertyName("sign")]
    public int Sign { get; set; }

    [JsonPropertyName("ref")]
    public string Ref { get; set; } = string.Empty;

    [JsonPropertyName("ts")]
    public string Ts { get; set; } = string.Empty;

    [JsonPropertyName("currency")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Currency { get; set; }
}

/// <summary>
/// The paginated ledger envelope for <c>GET /v1/jeeb/wallet/ledger</c>. Keys match
/// <c>DioWalletLedgerRepository._parse</c> (items, page, totalPages).
/// </summary>
public sealed class JeebWalletLedgerPageResponse
{
    [JsonPropertyName("items")]
    public IReadOnlyList<JeebWalletLedgerEntry> Items { get; set; } = new List<JeebWalletLedgerEntry>();

    [JsonPropertyName("page")]
    public int Page { get; set; }

    [JsonPropertyName("totalPages")]
    public int TotalPages { get; set; }
}
